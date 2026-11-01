using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace TbilisiFly.Core
{
    // ---- wire types -------------------------------------------------------
    // Mirror server/index.js's Mongoose schemas and `publicUser()` exactly —
    // field names match the JSON the server actually sends/accepts, so
    // Newtonsoft can (de)serialize them with no attributes.

    [Serializable]
    public class CharacterDef
    {
        public string hid;
        public string name;
        public string race;
        public string model;
        public string plane;
    }

    [Serializable]
    public class PublicUser
    {
        public string username;
        public string plane;
        public int level;
        public int stars;
        public List<CharacterDef> characters = new();
        public int activeChar = -1;
    }

    [Serializable]
    public class AuthResponse
    {
        public string token;
        public PublicUser user;
        public string error;
    }

    [Serializable]
    public class MeResponse
    {
        public PublicUser user;
        public string error;
    }

    [Serializable]
    public class OkResponse
    {
        public bool ok;
        public int? deleted;
        public int? updated;
        public int? imported;
        public string error;
    }

    [Serializable]
    public class HealthResponse
    {
        public bool ok;
        public bool db;
    }

    [Serializable]
    public class DamageRow
    {
        public float hp;
        public float[][] holes; // [[x,y,z,radius], ...]
    }

    [Serializable]
    public class DamagePatch
    {
        public float hp;
        public float[][] holes;
        public string addr;
        public float? x;
        public float? z;
    }

    [Serializable]
    public class AddressRow
    {
        public string addr;
        public float x;
        public float z;
    }

    /// <summary>Uniform result envelope — mirrors main.js's `api()` returning <c>{ ok, data }</c> (src/main.js:105-121).</summary>
    public readonly struct ApiResult<T>
    {
        public readonly bool Ok;
        public readonly T Data;
        public readonly string RawError;   // the server's own {error:"..."} string, if any (often Georgian)
        public readonly long StatusCode;

        public ApiResult(bool ok, T data, string rawError, long statusCode)
        {
            Ok = ok; Data = data; RawError = rawError; StatusCode = statusCode;
        }
    }

    /// <summary>
    /// Where the session token lives. Ports src/main.js:67-101's getToken /
    /// setToken / clearToken — net.js itself does not own the token (main.js
    /// does, and main.js is explicitly out of scope: "integrates everything,
    /// must come last"). It is reproduced here because without it the Unity
    /// client cannot actually authenticate against the unchanged backend.
    ///
    /// localStorage -> PlayerPrefs (survives app restarts, same as "remembered").
    /// sessionStorage -> a static field (survives scene loads within one running
    /// process, dies when the process exits — the closest a desktop app has to
    /// a browser tab's session storage; there is no browser-tab equivalent of
    /// "survives a reload but not a relaunch" in a compiled game).
    /// </summary>
    public static class TokenStore
    {
        const string TokenKey = "tf-token";
        const string LastUserKey = "tf-last-user";
        const string RememberKey = "tf-remember";
        static string _sessionToken; // sessionStorage stand-in

        /// <summary>src/main.js:82-84 — defaults to true ("!== '0'"), not false.</summary>
        public static bool Remembering => PlayerPrefs.GetInt(RememberKey, 1) != 0;

        public static string GetToken()
        {
            if (!string.IsNullOrEmpty(_sessionToken)) return _sessionToken;
            string v = PlayerPrefs.GetString(TokenKey, "");
            return string.IsNullOrEmpty(v) ? null : v;
        }

        public static void SetToken(string token, bool remember)
        {
            PlayerPrefs.SetInt(RememberKey, remember ? 1 : 0);
            _sessionToken = null;
            PlayerPrefs.DeleteKey(TokenKey);
            if (remember) { PlayerPrefs.SetString(TokenKey, token); PlayerPrefs.Save(); }
            else _sessionToken = token;
        }

        /// <summary>src/main.js:96-101 — clears ONLY the token, never REMEMBER_KEY or the last-user name.</summary>
        public static void ClearToken()
        {
            _sessionToken = null;
            PlayerPrefs.DeleteKey(TokenKey);
            PlayerPrefs.Save();
        }

        public static string LastUser
        {
            get => PlayerPrefs.GetString(LastUserKey, "");
            set { PlayerPrefs.SetString(LastUserKey, value ?? ""); PlayerPrefs.Save(); }
        }
    }

    /// <summary>
    /// Port of src/net.js's HTTP half plus the auth contract described in
    /// server/index.js (src/net.js itself is the WebSocket hero-state link —
    /// see HeroLink.cs; the REST calls below are what the task's scope note
    /// calls out explicitly: register/login/me/logout/health/damage/addresses).
    ///
    /// No coroutines required: UnityWebRequestAsyncOperation keeps running
    /// once SendWebRequest() is called even with nothing awaiting it, so every
    /// call here is callback-style and safe to fire from a plain C# class with
    /// no MonoBehaviour host — consistent with FlightModel.cs's plain-class
    /// convention.
    ///
    /// Base URL: the JS calls a RELATIVE `/api${path}` because the dev server
    /// proxies it to the same origin the page was served from. A compiled
    /// Unity build has no "origin" to inherit, so BaseUrl is an explicit
    /// field — set it once at startup (defaults to the backend's documented
    /// dev address).
    /// </summary>
    public static class NetClient
    {
        public static string BaseUrl = "http://localhost:3001/api";

        // The one error string main.js hands back when fetch() itself throws
        // (DNS failure, backend not running) — src/main.js:119, byte-for-byte,
        // Georgian, carried across.
        public const string UnreachableError = "სერვერი მიუწვდომელია — გაუშვი: npm run backend";

        static void Request<T>(string method, string path, object body, bool auth, Action<ApiResult<T>> cb)
        {
            UnityWebRequest req;
            string url = BaseUrl + path;
            if (body != null)
            {
                byte[] payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body));
                req = new UnityWebRequest(url, method)
                {
                    uploadHandler = new UploadHandlerRaw(payload),
                    downloadHandler = new DownloadHandlerBuffer(),
                };
                req.SetRequestHeader("Content-Type", "application/json");
            }
            else
            {
                req = new UnityWebRequest(url, method) { downloadHandler = new DownloadHandlerBuffer() };
            }
            if (auth)
            {
                string token = TokenStore.GetToken();
                if (!string.IsNullOrEmpty(token)) req.SetRequestHeader("Authorization", "Bearer " + token);
            }

            var op = req.SendWebRequest();
            op.completed += _ =>
            {
                bool networkOk = req.result == UnityWebRequest.Result.Success
                                  || req.result == UnityWebRequest.Result.ProtocolError; // HTTP 4xx/5xx still carries a JSON body
                string text = networkOk ? req.downloadHandler?.text : null;
                bool httpOk = req.result == UnityWebRequest.Result.Success; // fetch's res.ok: only 2xx
                T data = default;
                string rawError = null;
                if (!string.IsNullOrEmpty(text))
                {
                    try { data = JsonConvert.DeserializeObject<T>(text); }
                    catch { /* non-JSON body — leave data default, same as main.js's .catch(() => ({})) */ }
                    try
                    {
                        var probe = JsonConvert.DeserializeObject<Dictionary<string, object>>(text);
                        if (probe != null && probe.TryGetValue("error", out var e)) rawError = e?.ToString();
                    }
                    catch { /* ignore */ }
                }
                if (req.result == UnityWebRequest.Result.ConnectionError
                    || req.result == UnityWebRequest.Result.DataProcessingError)
                {
                    rawError = UnreachableError;
                }
                cb?.Invoke(new ApiResult<T>(httpOk, data, rawError, req.responseCode));
                req.Dispose();
            };
        }

        static string Query(params (string key, string val)[] kv)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var (k, v) in kv)
            {
                if (v == null) continue;
                sb.Append(first ? '?' : '&').Append(k).Append('=').Append(UnityWebRequest.EscapeURL(v));
                first = false;
            }
            return sb.ToString();
        }

        // ---- auth — server/index.js:163-267 -------------------------------

        public static void Register(string username, string password, bool remember, Action<ApiResult<AuthResponse>> cb)
            => Request<AuthResponse>("POST", "/auth/register", new { username, password }, false, r => OnAuth(r, remember, cb));

        public static void Login(string username, string password, bool remember, Action<ApiResult<AuthResponse>> cb)
            => Request<AuthResponse>("POST", "/auth/login", new { username, password }, false, r => OnAuth(r, remember, cb));

        static void OnAuth(ApiResult<AuthResponse> r, bool remember, Action<ApiResult<AuthResponse>> cb)
        {
            // src/main.js:236-243: on success, persist the token + last-used name.
            if (r.Ok && r.Data?.token != null)
            {
                TokenStore.SetToken(r.Data.token, remember);
                TokenStore.LastUser = r.Data.user?.username ?? TokenStore.LastUser;
            }
            cb?.Invoke(r);
        }

        public static void Me(Action<ApiResult<MeResponse>> cb)
            => Request<MeResponse>("GET", "/auth/me", null, true, cb);

        /// <summary>server/index.js:206-232 — equipped plane, level/stars, hero roster.</summary>
        public static void PatchMe(string plane, int? level, int? stars,
            List<CharacterDef> characters, int? activeChar, Action<ApiResult<MeResponse>> cb)
        {
            var body = new Dictionary<string, object>();
            if (plane != null) body["plane"] = plane;
            if (level.HasValue) body["level"] = level.Value;
            if (stars.HasValue) body["stars"] = stars.Value;
            if (characters != null) body["characters"] = characters;
            if (activeChar.HasValue) body["activeChar"] = activeChar.Value;
            Request<MeResponse>("PATCH", "/auth/me", body, true, cb);
        }

        public static void Logout(Action<ApiResult<OkResponse>> cb)
        {
            Request<OkResponse>("POST", "/auth/logout", new { }, true, r =>
            {
                TokenStore.ClearToken(); // src/main.js:807 — the ONE thing logout owns
                cb?.Invoke(r);
            });
        }

        public static void Health(Action<ApiResult<HealthResponse>> cb)
            => Request<HealthResponse>("GET", "/health", null, false, cb);

        // ---- damage — server/index.js:273-318, 373-381 --------------------

        public static void GetDamage(Action<ApiResult<Dictionary<string, DamageRow>>> cb)
            => Request<Dictionary<string, DamageRow>>("GET", "/damage", null, false, cb);

        public static void PutDamage(Dictionary<string, DamagePatch> damage, Action<ApiResult<OkResponse>> cb)
            => Request<OkResponse>("PUT", "/damage", new { damage }, false, cb);

        public static void DeleteDamage(Action<ApiResult<OkResponse>> cb)
            => Request<OkResponse>("DELETE", "/damage", null, false, cb);

        // ---- addresses — server/index.js:324-371 ---------------------------

        public static void GetAddresses(float x, float z, float r, Action<ApiResult<List<AddressRow>>> cb)
        {
            string q = Query(("x", x.ToString("R")), ("z", z.ToString("R")), ("r", r.ToString("R")));
            Request<List<AddressRow>>("GET", "/addresses" + q, null, false, cb);
        }

        /// <summary>No x/z -> the server returns {count}, not an array (server/index.js:359-361).</summary>
        public static void GetAddressCount(Action<ApiResult<Dictionary<string, int>>> cb)
            => Request<Dictionary<string, int>>("GET", "/addresses", null, false, cb);

        public static void ImportAddresses(List<object[]> items, Action<ApiResult<OkResponse>> cb)
            => Request<OkResponse>("POST", "/addresses/import", new { items }, false, cb);

        /// <summary>
        /// Last-gasp save, src/net.js:139-148's sendBeacon. UnityWebRequest has
        /// no delivery guarantee at shutdown (there is no sendBeacon
        /// equivalent in a compiled engine) — this is strictly best-effort,
        /// same spirit, weaker guarantee. See HeroLink.Beacon().
        /// </summary>
        public static void PostHeroStateBeacon(string token, string hid, object state)
            => Request<OkResponse>("POST", "/hero-state", new { token, hid, state }, false, _ => { });
    }
}
