// Keep the project on Unity's *plain Android* classic platform.
//
// Samsung Galaxy XR builds use the plain Android platform with the OpenXR
// loader and the Unity OpenXR: Android XR feature set. That is the
// configuration that has actually run on an SM-I610 (GalaxyXR_Audio). Unity
// 6.1+ also registers *derived* classic platforms on top of Android (the
// "Android XR" platform, and vendor headset platforms). Those add their own
// scripting defines and player / quality overrides, and the Android XR one
// re-applies its default feature set (scene meshing, planes, face tracking,
// ...) on every domain reload. So this wizard does not use them: when a
// derived platform or a custom build profile asset is active, it switches
// back to plain Android.
//
// IMPORTANT BACKGROUND
// --------------------
// 1. The public `BuildProfile.SetActiveBuildProfile(p)` REJECTS classic
//    profiles outright:
//      "[BuildProfile] Classic Platforms cannot be set as the active build
//       profile."
//    See `BuildProfileContext.activeProfile` setter in the Unity reference
//    source — it logs that exact warning if you try.
//
// 2. The supported way to switch to a *classic* platform is the internal
//    native binding `EditorUserBuildSettings.SwitchActiveBuildTargetGuid
//    (BuildProfile)`, wrapped publicly in 6000.2 by
//    `BuildProfileModuleUtil.SwitchLegacyActiveFromBuildProfile(p)`. Both
//    are reached here via reflection because the wrapper status flips
//    between Unity versions.
//
// 3. The plain Android platform GUID is hardcoded in
//    `BuildTargetDiscovery.bindings.cs` as
//    "b9b35072a6f44c2e863f17467ea3dc13" (verified against the 6000.4
//    UnityEditor.CoreModule string table). We hardcode the same constant
//    so we don't depend on localised display names.

using System;
using System.Collections;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEngine;

namespace Genesis.RoomScan.Editor
{
    public partial class RoomScanSetupWizard
    {
        // Stable GUID Unity uses internally for the plain Android classic
        // platform (see Editor/Mono/BuildTargetDiscovery.bindings.cs in the
        // Unity reference source).
        const string k_AndroidPlatformGuid = "b9b35072a6f44c2e863f17467ea3dc13";

        // Unity's derived "Android XR" classic platform. Only used to give a
        // readable label when it is the active platform.
        const string k_AndroidXRDerivedPlatformGuid = "a71389c8cc8e4edc99d30db86d62ee8f";

        const string k_EmptyGuid = "00000000000000000000000000000000";

        // Cached reflective handles. Populated lazily by
        // ResolveBuildProfileApi() and reused.
        static bool _bpResolved;
        static Type _bpContextType;
        static object _bpContextInstance;
        static MethodInfo _bpGetForClassicPlatformByGuid;
        static MethodInfo _bpSwitchActiveByProfile;          // EditorUserBuildSettings.SwitchActiveBuildTargetGuid(BuildProfile)
        static MethodInfo _bpModuleUtilSwitchLegacy;         // BuildProfileModuleUtil.SwitchLegacyActiveFromBuildProfile(BuildProfile) — public wrapper in 6000.2
        static PropertyInfo _bpActivePlatformGuidProp;       // EditorUserBuildSettings.activePlatformGuid (internal)
        static Type _bpGuidType;
        static ConstructorInfo _bpGuidStringCtor;

        static bool ResolveBuildProfileApi()
        {
            if (_bpResolved) return _bpContextInstance != null;
            _bpResolved = true;

            try
            {
                var bpAsm = typeof(BuildProfile).Assembly;
                var eubsAsm = typeof(EditorUserBuildSettings).Assembly;

                // UnityEditor.GUID lives in the editor assembly.
                _bpGuidType = eubsAsm.GetType("UnityEditor.GUID")
                              ?? bpAsm.GetType("UnityEditor.GUID");
                _bpGuidStringCtor = _bpGuidType?.GetConstructor(new[] { typeof(string) });

                _bpContextType = bpAsm.GetType("UnityEditor.Build.Profile.BuildProfileContext");
                if (_bpContextType == null)
                {
                    Debug.LogWarning("[RoomScan Setup] BuildProfileContext type not found.");
                    return false;
                }

                var instanceProp = _bpContextType.GetProperty("instance",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                _bpContextInstance = instanceProp?.GetValue(null);
                if (_bpContextInstance == null)
                {
                    Debug.LogWarning("[RoomScan Setup] BuildProfileContext.instance not accessible.");
                    return false;
                }

                // GetForClassicPlatform(GUID) — instance, internal.
                if (_bpGuidType != null)
                {
                    _bpGetForClassicPlatformByGuid = _bpContextType.GetMethod(
                        "GetForClassicPlatform",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, new[] { _bpGuidType }, null);
                }

                // EditorUserBuildSettings.SwitchActiveBuildTargetGuid(BuildProfile)
                // — internal static. This is the one the editor itself
                // calls from BuildProfile.OnValidate / BuildProfileWindow.
                _bpSwitchActiveByProfile = typeof(EditorUserBuildSettings).GetMethod(
                    "SwitchActiveBuildTargetGuid",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(BuildProfile) }, null);

                // BuildProfileModuleUtil.SwitchLegacyActiveFromBuildProfile(BuildProfile)
                // is the friendlier wrapper exposed in 6000.2; in newer
                // builds it may move or change visibility.
                var moduleUtilType = bpAsm.GetType("UnityEditor.Build.Profile.BuildProfileModuleUtil");
                _bpModuleUtilSwitchLegacy = moduleUtilType?.GetMethod(
                    "SwitchLegacyActiveFromBuildProfile",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(BuildProfile) }, null);

                _bpActivePlatformGuidProp = typeof(EditorUserBuildSettings).GetProperty(
                    "activePlatformGuid",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RoomScan Setup] BuildProfile API resolve failed: {ex.Message}");
                _bpContextInstance = null;
                return false;
            }
        }

        // Returns a reflected UnityEditor.GUID for the plain Android
        // platform. Returns null if the GUID type isn't available.
        static object AndroidPlatformGuid()
        {
            if (_bpGuidStringCtor == null) return null;
            try { return _bpGuidStringCtor.Invoke(new object[] { k_AndroidPlatformGuid }); }
            catch { return null; }
        }

        /// <summary>
        /// The active classic platform GUID as 32 hex chars, or null when
        /// the internal API is unavailable.
        /// </summary>
        static string ActivePlatformGuidString()
        {
            if (!ResolveBuildProfileApi() || _bpActivePlatformGuidProp == null) return null;
            try { return _bpActivePlatformGuidProp.GetValue(null)?.ToString(); }
            catch { return null; }
        }

        static BuildProfile ActiveCustomBuildProfile()
        {
            try { return BuildProfile.GetActiveBuildProfile(); }
            catch { return null; }
        }

        /// <summary>
        /// True when the active build target is Android, no custom build
        /// profile asset is active, and the active classic platform is plain
        /// Android rather than a derived platform. An empty / unreadable
        /// platform GUID (projects that never touched Build Profiles) counts
        /// as plain Android.
        /// </summary>
        internal static bool IsActivePlatformPlainAndroid()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android) return false;
            if (ActiveCustomBuildProfile() != null) return false;

            var guid = ActivePlatformGuidString();
            if (string.IsNullOrEmpty(guid) || guid == k_EmptyGuid) return true;
            return string.Equals(guid, k_AndroidPlatformGuid, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Human-readable name of the active platform / profile.</summary>
        internal static string DescribeActivePlatform()
        {
            var custom = ActiveCustomBuildProfile();
            if (custom != null) return $"custom build profile '{custom.name}'";

            var target = EditorUserBuildSettings.activeBuildTarget;
            if (target != BuildTarget.Android) return target.ToString();

            var guid = ActivePlatformGuidString();
            if (string.IsNullOrEmpty(guid) || guid == k_EmptyGuid
                || string.Equals(guid, k_AndroidPlatformGuid, StringComparison.OrdinalIgnoreCase))
                return "Android (plain)";
            if (string.Equals(guid, k_AndroidXRDerivedPlatformGuid, StringComparison.OrdinalIgnoreCase))
                return "Android XR derived platform";
            return $"derived Android platform {guid}";
        }

        /// <summary>
        /// Returns the classic plain-Android BuildProfile Unity registers
        /// when the Android module is installed, or null.
        /// </summary>
        static BuildProfile FindPlainAndroidClassicProfile()
        {
            if (!ResolveBuildProfileApi()) return null;

            try
            {
                var guid = AndroidPlatformGuid();
                if (guid != null && _bpGetForClassicPlatformByGuid != null)
                {
                    var profile = _bpGetForClassicPlatformByGuid.Invoke(_bpContextInstance, new[] { guid }) as BuildProfile;
                    if (profile != null) return profile;
                }

                // Last-ditch fallback: scan classicPlatformProfiles by
                // platformGuid (covers obscure setups where the GUID-keyed
                // dictionary isn't populated yet).
                var classicsProp = _bpContextType.GetProperty("classicPlatformProfiles",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (classicsProp?.GetValue(_bpContextInstance) is IEnumerable classics)
                {
                    var guidStrProp = typeof(BuildProfile).GetProperty("platformGuid",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    foreach (var item in classics)
                    {
                        if (item is not BuildProfile p) continue;
                        var g = guidStrProp?.GetValue(p)?.ToString();
                        if (string.Equals(g, k_AndroidPlatformGuid, StringComparison.OrdinalIgnoreCase))
                            return p;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RoomScan Setup] Android classic profile lookup failed: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Makes plain Android the active platform. Uses the public
        /// SwitchActiveBuildTarget when the active target is not Android at
        /// all, and the internal classic-platform switch when Android is
        /// active through a derived platform or a custom profile. Returns
        /// true when a switch was issued (a domain reload will follow).
        /// </summary>
        internal static bool TryActivatePlainAndroidPlatform()
        {
            if (IsActivePlatformPlainAndroid()) return false;

            string from = DescribeActivePlatform();

            // Prefer the classic-profile switch: it also leaves a derived
            // platform or a custom profile, which SwitchActiveBuildTarget
            // does not when the target is already Android.
            var profile = FindPlainAndroidClassicProfile();
            if (profile == null)
            {
                // Profile dictionary is populated lazily on first access to
                // the BuildProfileContext UI. Touch a public API to trigger
                // lazy creation, then retry once.
                try { _ = BuildProfile.GetActiveBuildProfile(); } catch { /* ignore */ }
                profile = FindPlainAndroidClassicProfile();
            }

            if (profile != null)
            {
                try
                {
                    if (_bpModuleUtilSwitchLegacy != null)
                    {
                        _bpModuleUtilSwitchLegacy.Invoke(null, new object[] { profile });
                        Debug.Log($"[RoomScan Setup] Switched {from} → plain Android platform " +
                                  "(via BuildProfileModuleUtil.SwitchLegacyActiveFromBuildProfile).");
                        return true;
                    }

                    if (_bpSwitchActiveByProfile != null)
                    {
                        var ok = _bpSwitchActiveByProfile.Invoke(null, new object[] { profile });
                        if (ok is not bool b || b)
                        {
                            Debug.Log($"[RoomScan Setup] Switched {from} → plain Android platform " +
                                      "(via EditorUserBuildSettings.SwitchActiveBuildTargetGuid).");
                            return true;
                        }
                        Debug.LogWarning("[RoomScan Setup] SwitchActiveBuildTargetGuid returned false.");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[RoomScan Setup] Plain Android platform activation failed: " +
                                     (ex.InnerException?.Message ?? ex.Message));
                }
            }

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            {
                bool switched = EditorUserBuildSettings.SwitchActiveBuildTarget(
                    BuildTargetGroup.Android, BuildTarget.Android);
                Debug.Log($"[RoomScan Setup] SwitchActiveBuildTarget {from} → Android: {(switched ? "ok" : "FAILED")}.");
                return switched;
            }

            Debug.LogWarning($"[RoomScan Setup] Active platform is {from} and no internal API is " +
                             "available to switch it (Unity " + Application.unityVersion + "). " +
                             "Select plain Android in File > Build Profiles by hand.");
            return false;
        }
    }
}
