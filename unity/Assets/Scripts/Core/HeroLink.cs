using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace TbilisiFly.Core
{
    /// <summary>
    /// Port of src/net.js (233 lines) — the hero-state link over /ws.
    ///
    /// Every personage keeps its own spawn place: position, heading, camera,
    /// whether the hero was standing outside the plane. The game streams a
    /// snapshot every couple of seconds; on connect, the server hands back
    /// every hero's last snapshot, so picking a hero drops you exactly where
    /// it stopped — WoW-style, one saved spot per character.
    ///
    /// `state` is kept as an opaque Newtonsoft JObject throughout, same as the
    /// JS keeps it an opaque JS object — this class never needs to know its
    /// shape beyond an `at` timestamp, exactly like net.js.
    ///
    /// THREADING: ClientWebSocket's send/receive are async and run off Unity's
    /// main thread. Nothing here touches a Unity API from the socket's own
    /// callbacks — incoming frames are queued into a ConcurrentQueue and only
    /// parsed/merged inside Tick(dt), which the host calls once a frame on the
    /// main thread (same pattern FlightModel.cs expects: a plain class, driven
    /// externally). PlayerPrefs/File IO for the local mirror also only happen
    /// from Tick() or from methods the host itself calls on the main thread.
    ///
    /// NO BROWSER TAB, NO sendBeacon: there is no engine-level guarantee that
    /// an HTTP request issued while the process is exiting is delivered (see
    /// NetClient.PostHeroStateBeacon's note). The JSON mirror on disk is what
    /// actually survives reliably here, same role localStorage played in net.js.
    /// </summary>
    public class HeroLink
    {
        public Dictionary<string, JObject> States = new();
        public string Username;
        public string Token;
        public bool Connected;

        /// <summary>Fired when the server's (or the local mirror's) snapshots land — src/net.js:22.</summary>
        public Action<Dictionary<string, JObject>> OnStates;

        /// <summary>
        /// The live game, asked to take a snapshot RIGHT NOW — src/net.js:27,127
        /// `provider`. A void callback: calling it is expected to call Save()
        /// itself as a side effect (src/main.js:717-719's
        /// `game.saveState(true, 'leaving the page')`), not to hand one back.
        /// </summary>
        public Action Provider;

        readonly Dictionary<string, JObject> _queue = new(); // hid -> snapshot waiting for an open socket (null = delete)
        int _retry;
        float _retryWaitRemaining = -1f; // seconds; <0 = no retry scheduled
        bool _wanted;
        float _leftAtRealtime = -1000f;

        ClientWebSocket _ws;
        CancellationTokenSource _wsCts;
        readonly SemaphoreSlim _sendLock = new(1, 1);
        readonly ConcurrentQueue<string> _incoming = new();
        volatile bool _socketDied;

        // ---- lifecycle ------------------------------------------------------

        /// <summary>src/net.js:43-58 — sign in: local mirror first, then connect.</summary>
        public void Open(string token, string username)
        {
            if (_ws != null && Token != token)
            {
                // switching accounts: the old socket's handlers must not touch
                // the new state -- tear it down without a reconnect.
                _wsCts?.Cancel();
                _ws = null;
                _queue.Clear();
            }
            Token = token;
            Username = username;
            States = ReadLocal();
            _wanted = true;
            Connect();
        }

        /// <summary>src/net.js:66-77 — flush, then forget the account LAST.</summary>
        public void Close()
        {
            _wanted = false;
            _retryWaitRemaining = -1f;
            Flush();
            Provider = null;
            _wsCts?.Cancel();
            _ws = null;
            Connected = false;
            States = new Dictionary<string, JObject>();
            Username = null;
            Token = null;
        }

        public JObject StateOf(string hid)
            => (!string.IsNullOrEmpty(hid) && States.TryGetValue(hid, out var s)) ? s : null;

        /// <summary>src/net.js:85-108 — local first, socket second. `state == null` clears (the hero died).</summary>
        public void Save(string hid, JObject state)
        {
            if (string.IsNullOrEmpty(hid) || string.IsNullOrEmpty(Username)) return; // logged out: nobody's hero now
            if (state != null)
            {
                // AN EMPTY KIT NEVER OVERWRITES A STORED ONE (src/net.js:88-99).
                if (States.TryGetValue(hid, out var prev) && prev != null)
                {
                    if (!(state["guns"] is JArray g1 && g1.Count > 0) && prev["guns"] is JArray g0 && g0.Count > 0)
                        state["guns"] = g0;
                    if (!(state["gear"] is JArray r1 && r1.Count > 0) && prev["gear"] is JArray r0 && r0.Count > 0)
                        state["gear"] = r0;
                }
                state["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                States[hid] = state;
            }
            else
            {
                States.Remove(hid);
            }
            WriteLocal();
            _queue[hid] = state; // null allowed -> delete
            Send();
        }

        /// <summary>
        /// src/net.js:116-133 — push everything pending right now. `leaving`
        /// marks an app-exit flush: de-duplicated to one per ~1s window, same
        /// as the JS guards pagehide+beforeunload+visibilitychange firing together.
        /// </summary>
        public void Flush(bool leaving = false)
        {
            if (leaving)
            {
                float now = Time.realtimeSinceStartup;
                if (now - _leftAtRealtime < 1f) return;
                _leftAtRealtime = now;
            }
            try { Provider?.Invoke(); } // expected to call Save() itself, synchronously
            catch (Exception e) { Debug.LogWarning($"snapshot on unload failed: {e.Message}"); }
            if (leaving) Beacon();
            Send();
        }

        /// <summary>src/net.js:139-148. Best-effort — see the class header.</summary>
        void Beacon()
        {
            if (_queue.Count == 0 || string.IsNullOrEmpty(Token)) return;
            foreach (var kv in new List<KeyValuePair<string, JObject>>(_queue))
            {
                NetClient.PostHeroStateBeacon(Token, kv.Key, kv.Value);
            }
        }

        /// <summary>Call once a frame, on the main thread. Drains inbound WS frames and the retry timer.</summary>
        public void Tick(float dt)
        {
            while (_incoming.TryDequeue(out string raw))
            {
                JObject msg;
                try { msg = JObject.Parse(raw); } catch { continue; }
                string t = msg["t"]?.ToString();
                if (t == "hello" || t == "states") Merge(msg["states"] as JObject);
            }
            if (_socketDied)
            {
                _socketDied = false;
                Connected = false;
                _ws = null;
                ScheduleRetry();
            }
            if (_retryWaitRemaining >= 0f)
            {
                _retryWaitRemaining -= dt;
                if (_retryWaitRemaining <= 0f)
                {
                    _retryWaitRemaining = -1f;
                    Connect();
                }
            }
        }

        // ---- socket ----------------------------------------------------------

        async void Connect()
        {
            if (!_wanted || string.IsNullOrEmpty(Token) || _ws != null) return;
            string url = NetClient.BaseUrl.Replace("http://", "ws://").Replace("https://", "wss://");
            // strip the trailing "/api" NetClient.BaseUrl carries -- /ws is a
            // sibling route, not under /api (server/index.js:403).
            if (url.EndsWith("/api")) url = url.Substring(0, url.Length - 4);
            url += "/ws?token=" + Uri.EscapeDataString(Token);

            var ws = new ClientWebSocket();
            _wsCts = new CancellationTokenSource();
            try
            {
                await ws.ConnectAsync(new Uri(url), _wsCts.Token);
            }
            catch
            {
                _socketDied = true;
                return;
            }
            _ws = ws;
            Connected = true;
            _retry = 0;
            Send();
            _ = ReceiveLoop(ws, _wsCts.Token);
        }

        async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
        {
            var buf = new byte[8192];
            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                        if (r.MessageType == WebSocketMessageType.Close) { return; }
                        ms.Write(buf, 0, r.Count);
                    } while (!r.EndOfMessage);
                    _incoming.Enqueue(Encoding.UTF8.GetString(ms.ToArray()));
                }
            }
            catch { /* onerror/onclose in the JS just retries; so do we, below */ }
            finally
            {
                // src/net.js:176-181 — code 4001 (stale token) means don't retry;
                // ClientWebSocket surfaces that as CloseStatus after the loop ends.
                bool staleToken = ws.CloseStatus.HasValue && (int)ws.CloseStatus.Value == 4001;
                if (!staleToken) _socketDied = true;
                else { Connected = false; _ws = null; }
            }
        }

        void ScheduleRetry()
        {
            if (!_wanted) return;
            float wait = Mathf.Min(30f, Mathf.Pow(2f, _retry++)); // src/net.js:188 (seconds, not ms)
            _retryWaitRemaining = wait;
        }

        void Send()
        {
            if (_queue.Count == 0) return;
            if (_ws == null || _ws.State != WebSocketState.Open) return; // keep it queued
            var pending = new List<KeyValuePair<string, JObject>>(_queue);
            _queue.Clear();
            foreach (var (hid, state) in pending)
            {
                var msg = new JObject { ["t"] = "save", ["hid"] = hid, ["state"] = (JToken)state ?? JValue.CreateNull() };
                _ = SendText(msg.ToString(Formatting.None));
            }
        }

        async Task SendText(string json)
        {
            if (_ws == null) return;
            await _sendLock.WaitAsync();
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    var bytes = Encoding.UTF8.GetBytes(json);
                    await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            catch { _socketDied = true; }
            finally { _sendLock.Release(); }
        }

        /// <summary>src/net.js:203-217 — the server is the shared truth, but a LOCAL write while the backend was down is newer.</summary>
        void Merge(JObject remote)
        {
            if (remote == null) return;
            foreach (var prop in remote.Properties())
            {
                string hid = prop.Name;
                var state = prop.Value as JObject;
                bool haveMine = States.TryGetValue(hid, out var mine);
                long stateAt = state?["at"]?.Value<long>() ?? 0;
                long mineAt = mine?["at"]?.Value<long>() ?? 0;
                if (!haveMine || stateAt >= mineAt) States[hid] = state;
                else _queue[hid] = mine; // ours is newer -- push it back up
            }
            foreach (var kv in States)
            {
                if (remote[kv.Key] == null) _queue[kv.Key] = kv.Value; // local-only, backend was down
            }
            WriteLocal();
            Send();
            OnStates?.Invoke(States);
        }

        // ---- local mirror: a JSON file stands in for localStorage ------------

        static string LocalPath(string username)
            => Path.Combine(Application.persistentDataPath, "heroes", $"tf-heroes-{username ?? ""}.json");

        Dictionary<string, JObject> ReadLocal()
        {
            try
            {
                string path = LocalPath(Username);
                if (!File.Exists(path)) return new Dictionary<string, JObject>();
                var obj = JObject.Parse(File.ReadAllText(path));
                var outDict = new Dictionary<string, JObject>();
                foreach (var p in obj.Properties()) outDict[p.Name] = p.Value as JObject;
                return outDict;
            }
            catch { return new Dictionary<string, JObject>(); }
        }

        void WriteLocal()
        {
            if (string.IsNullOrEmpty(Username)) return; // no account open -- never write a stray mirror
            try
            {
                string path = LocalPath(Username);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var obj = new JObject();
                foreach (var kv in States) obj[kv.Key] = kv.Value;
                File.WriteAllText(path, obj.ToString(Formatting.None));
            }
            catch { /* disk full/blocked -- the socket still has it */ }
        }
    }

}
