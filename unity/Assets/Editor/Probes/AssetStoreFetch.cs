using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// One-off tool (not a port probe) to fetch the purchased "Parkour Animation
    /// Set" (publisher Threepeat) .unitypackage into the local Asset Store cache
    /// (~/Library/Unity/Asset Store-5.x/) via the editor's OWN Package Manager
    /// machinery — never by hand, never via the website.
    ///
    /// Everything here is internal/private Unity API reached by reflection,
    /// because UnityEditor.Connect.UnityConnect and the Package Manager UI's
    /// AssetStoreClient/AssetStoreDownloadManager are not public.
    ///
    ///   Unity -batchmode -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.AssetStoreFetch.CheckLogin
    ///        -logFile ...
    ///
    ///   Unity -batchmode -nographics -projectPath unity
    ///        -executeMethod TbilisiFly.Probes.AssetStoreFetch.FetchParkour
    ///        -logFile ...
    ///
    /// Neither method passes -quit: batch mode would tear the process down
    /// before the async download machinery finishes, so each method keeps the
    /// process alive itself and calls EditorApplication.Exit at the end.
    /// </summary>
    public static class AssetStoreFetch
    {
        // ---------------------------------------------------------------
        // Step 1: confirm the editor is logged into the Unity account that
        // owns the purchase.
        // ---------------------------------------------------------------
        public static void CheckLogin()
        {
            try
            {
                Type ucType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.Connect.UnityConnect");
                if (ucType == null)
                {
                    Debug.Log("LOGIN_CHECK result=FAIL reason=type_not_found UnityConnect");
                    EditorApplication.Exit(1);
                    return;
                }

                PropertyInfo instanceProp = ucType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static);
                object instance = instanceProp?.GetValue(null);
                if (instance == null)
                {
                    Debug.Log("LOGIN_CHECK result=FAIL reason=no_instance");
                    EditorApplication.Exit(1);
                    return;
                }

                PropertyInfo loggedInProp = ucType.GetProperty("loggedIn", BindingFlags.Public | BindingFlags.Instance);
                MethodInfo isUserLoggedInMethod = ucType.GetMethod("IsUserLoggedIn", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                PropertyInfo onlineProp = ucType.GetProperty("online", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                // UnityConnect's handshake is async; poll for up to ~20s instead
                // of trusting a single read taken the instant the process starts.
                bool loggedIn = false;
                bool online = false;
                for (int i = 0; i < 20; i++)
                {
                    loggedIn = loggedInProp != null && (bool)loggedInProp.GetValue(instance);
                    online = onlineProp != null && (bool)onlineProp.GetValue(instance);
                    bool viaMethod = isUserLoggedInMethod != null && (bool)isUserLoggedInMethod.Invoke(instance, null);
                    Debug.Log($"LOGIN_CHECK poll i={i} loggedIn={loggedIn.ToString().ToLowerInvariant()} viaMethod={viaMethod.ToString().ToLowerInvariant()} online={online.ToString().ToLowerInvariant()}");
                    if (loggedIn) break;
                    Thread.Sleep(1000);
                }

                string displayName = "<unknown>";
                PropertyInfo userInfoProp = ucType.GetProperty("userInfo", BindingFlags.Public | BindingFlags.Instance);
                if (userInfoProp != null)
                {
                    object userInfo = userInfoProp.GetValue(instance);
                    if (userInfo != null)
                    {
                        PropertyInfo displayNameProp = userInfo.GetType().GetProperty("displayName", BindingFlags.Public | BindingFlags.Instance);
                        FieldInfo displayNameField = userInfo.GetType().GetField("displayName", BindingFlags.Public | BindingFlags.Instance);
                        displayName = (displayNameProp?.GetValue(userInfo) ?? displayNameField?.GetValue(userInfo) ?? "<null>").ToString();
                    }
                }

                string tokenState = "<no_method>";
                MethodInfo getTokenMethod = ucType.GetMethod("GetAccessToken", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                if (getTokenMethod != null)
                {
                    object tokenObj = getTokenMethod.Invoke(instance, null);
                    string token = tokenObj as string;
                    tokenState = string.IsNullOrEmpty(token) ? "empty" : $"non_empty(len={token.Length})";
                }

                Debug.Log($"LOGIN_CHECK loggedIn={loggedIn.ToString().ToLowerInvariant()} online={online.ToString().ToLowerInvariant()} displayName={displayName} accessToken={tokenState}");
                EditorApplication.Exit(loggedIn ? 0 : 2);
            }
            catch (Exception e)
            {
                Debug.Log("LOGIN_CHECK result=EXCEPTION " + e);
                EditorApplication.Exit(1);
            }
        }

        // ---------------------------------------------------------------
        // Step 2: inspect the Package Manager UI internal services so we know
        // the REAL type/method names before calling anything (per the brief:
        // "inspect ... with reflection and print them, do not guess").
        // ---------------------------------------------------------------
        public static void InspectServices()
        {
            try
            {
                Assembly pmui = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "UnityEditor.PackageManagerUIModule");
                if (pmui == null)
                {
                    Debug.Log("INSPECT result=FAIL reason=assembly_not_found UnityEditor.PackageManagerUIModule");
                    EditorApplication.Exit(1);
                    return;
                }
                Debug.Log("INSPECT assembly=" + pmui.FullName);

                Type[] allTypes;
                try { allTypes = pmui.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { allTypes = rtle.Types.Where(t => t != null).ToArray(); }

                Type servicesContainerType = allTypes.FirstOrDefault(t => t.Name == "ServicesContainer");
                if (servicesContainerType == null)
                {
                    Debug.Log("INSPECT result=FAIL reason=ServicesContainer_not_found");
                    // dump candidate type names to help a human/agent retarget
                    foreach (var t in allTypes.Where(t => t.Namespace != null && t.Namespace.Contains("Internal")))
                        Debug.Log("INSPECT candidate_type=" + t.FullName);
                    EditorApplication.Exit(1);
                    return;
                }
                Debug.Log("INSPECT ServicesContainer=" + servicesContainerType.FullName);

                foreach (var name in new[] { "AssetStoreClient", "AssetStoreCache", "AssetStoreDownloadManager", "AssetStoreRestAPI", "AssetStoreOperationErrorHandler", "AssetStoreCallQueue", "UnityConnectProxy" })
                {
                    Type t = allTypes.FirstOrDefault(tt => tt.Name == name);
                    if (t == null) { Debug.Log($"INSPECT type={name} NOT_FOUND"); continue; }
                    Debug.Log($"INSPECT type={name} full={t.FullName}");
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                                        .Where(m => !m.IsSpecialName)
                                        .OrderBy(m => m.Name))
                    {
                        var ps = string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
                        Debug.Log($"INSPECT   method {t.Name}.{m.Name}({ps}) -> {m.ReturnType.Name}");
                    }
                }

                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.Log("INSPECT result=EXCEPTION " + e);
                EditorApplication.Exit(1);
            }
        }

        // ---------------------------------------------------------------
        // Step 3: list purchases, find "Parkour", start the download, poll
        // AssetStoreDownloadManager until done (file appears in the cache) or
        // 10 minutes elapse, then exit. Kept alive by pumping EditorApplication.update.
        // ---------------------------------------------------------------
        private static volatile bool _done = false;
        private static DateTime _deadline;

        public static void FetchParkour()
        {
            try
            {
                _deadline = DateTime.UtcNow.AddMinutes(10);
                EditorApplication.update += Pump;
                Debug.Log("FETCH_START");
            }
            catch (Exception e)
            {
                Debug.Log("FETCH result=EXCEPTION_AT_START " + e);
                EditorApplication.Exit(1);
            }
        }

        private static object _servicesContainer;
        private static object _assetStoreClient;
        private static object _assetStoreCache;
        private static object _assetStoreDownloadManager;
        private static bool _initialized = false;
        private static bool _listRequested = false;
        private static string _parkourProductId = null;
        private static bool _downloadStarted = false;

        private static void Pump()
        {
            try
            {
                if (DateTime.UtcNow > _deadline)
                {
                    Debug.Log("FETCH result=TIMEOUT after 10 minutes");
                    EditorApplication.update -= Pump;
                    EditorApplication.Exit(3);
                    return;
                }

                if (!_initialized)
                {
                    InitServices();
                    _initialized = true;
                    return;
                }

                if (_parkourProductId == null)
                {
                    TryFindParkour();
                    return;
                }

                if (!_downloadStarted)
                {
                    StartDownload();
                    return;
                }

                PollDownload();
            }
            catch (Exception e)
            {
                Debug.Log("FETCH result=EXCEPTION_IN_PUMP " + e);
                EditorApplication.update -= Pump;
                EditorApplication.Exit(1);
            }
        }

        private static void InitServices()
        {
            Assembly pmui = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "UnityEditor.PackageManagerUIModule");
            Type[] allTypes;
            try { allTypes = pmui.GetTypes(); }
            catch (ReflectionTypeLoadException rtle) { allTypes = rtle.Types.Where(t => t != null).ToArray(); }

            Type servicesContainerType = allTypes.First(t => t.Name == "ServicesContainer");
            PropertyInfo instanceProp = servicesContainerType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static);
            _servicesContainer = instanceProp.GetValue(null);
            Debug.Log("FETCH services_container=ok");

            MethodInfo resolveMethod = servicesContainerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(m => m.Name == "Resolve" && m.IsGenericMethodDefinition);

            Type clientType = allTypes.First(t => t.Name == "AssetStoreClient");
            Type cacheType = allTypes.First(t => t.Name == "AssetStoreCache");
            Type dlType = allTypes.First(t => t.Name == "AssetStoreDownloadManager");

            _assetStoreClient = resolveMethod.MakeGenericMethod(clientType).Invoke(_servicesContainer, null);
            _assetStoreCache = resolveMethod.MakeGenericMethod(cacheType).Invoke(_servicesContainer, null);
            _assetStoreDownloadManager = resolveMethod.MakeGenericMethod(dlType).Invoke(_servicesContainer, null);

            Debug.Log("FETCH resolved client=" + (_assetStoreClient != null) + " cache=" + (_assetStoreCache != null) + " dlmgr=" + (_assetStoreDownloadManager != null));
        }

        private static void TryFindParkour()
        {
            if (!_listRequested)
            {
                _listRequested = true;
                Type clientType = _assetStoreClient.GetType();
                // Try the common method name for listing purchases; log the
                // type's method surface first so a failure is diagnosable.
                var methods = clientType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(m => m.Name.IndexOf("List", StringComparison.OrdinalIgnoreCase) >= 0
                             || m.Name.IndexOf("Purchase", StringComparison.OrdinalIgnoreCase) >= 0
                             || m.Name.IndexOf("Search", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                foreach (var m in methods)
                    Debug.Log($"FETCH candidate_list_method {m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");

                MethodInfo listMethod = methods.FirstOrDefault(m => m.Name == "ListPurchases");
                if (listMethod == null)
                {
                    Debug.Log("FETCH result=FAIL reason=no_ListPurchases_method");
                    EditorApplication.update -= Pump;
                    EditorApplication.Exit(4);
                    return;
                }

                // Build the query object type the method expects and invoke it
                // with an Action<T> callback captured via a local delegate.
                var ps = listMethod.GetParameters();
                Debug.Log("FETCH ListPurchases params=" + string.Join(",", ps.Select(p => p.ParameterType.FullName)));
                EditorApplication.update -= Pump;
                Debug.Log("FETCH result=NEEDS_MANUAL_WIRING — inspect params above before proceeding");
                EditorApplication.Exit(5);
            }
        }

        private static void StartDownload() { }
        private static void PollDownload() { }
    }
}
