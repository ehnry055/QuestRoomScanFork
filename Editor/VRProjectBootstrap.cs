// Android XR (Samsung Galaxy XR) project bootstrap.
//
// Audits and fixes project-level XR config that the rest of the
// RoomScanSetupWizard does not handle: the active platform, PlayerSettings,
// XR Plug-in Management loaders, and the OpenXR feature / interaction
// profile set of Unity OpenXR: Android XR (com.unity.xr.androidxr-openxr).
//
// The only target is Samsung Galaxy XR (SM-I610, Android XR, API 34). Every
// Meta Quest OpenXR feature is actively disabled: Meta and Android XR both
// register their AR providers under the Android build target group, and
// XR Management keeps one subsystem instance per type, so with both enabled
// whichever registers last wins.
//
// The feature list mirrors the set that has run on the headset
// (GalaxyXR_Audio: Android XR Support, AR Session / Camera / Anchor, Hand
// Tracking, Hand Interaction, Foveated Rendering, ARMesh off, Single Pass
// Instanced, depth submission None), plus AR Occlusion for environment
// depth and the Oculus Touch profile, which is what Android XR uses for
// Galaxy XR controllers.
//
// Scope: the only per-game identity written is a default Android
// applicationIdentifier, and only while the project still has Unity's
// placeholder id.
//
// All fixes are idempotent: rerunning Audit + Fix All is safe.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;

namespace Genesis.RoomScan.Editor
{
    internal enum CheckSeverity { Outstanding, Recommended }
    internal enum CheckResult { Ok, Failing, NotFixable }

    internal sealed class VRCheck
    {
        public string Id;
        public string Label;
        public CheckSeverity Severity;
        public BuildTargetGroup Group;
        public Func<string> CurrentValue;
        public string TargetValue;
        public Func<bool> IsOk;
        public Action Fix;
    }

    /// <summary>Outcome of <see cref="VRProjectBootstrap.FixAll"/>.</summary>
    internal sealed class VRFixReport
    {
        public int Fixed;
        public int Skipped;
        /// <summary>A build-target / platform switch was issued; a domain
        /// reload follows, so re-run to verify the rest.</summary>
        public bool PlatformSwitched;
        /// <summary>Checks still failing after their Fix ran (or that have
        /// no Fix and are failing).</summary>
        public readonly List<VRCheck> StillFailing = new();
    }

    internal static class VRProjectBootstrap
    {
        // OpenXR feature ids — kept here so the editor assembly takes no
        // hard reference on the Android XR provider assembly just to read
        // constants. Lookups fall back to the feature's type name (see
        // k_FeatureTypeNames) in case an id changes between package
        // versions. Values checked against com.unity.xr.androidxr-openxr
        // 1.2.0, com.unity.xr.openxr 1.18.0 and com.unity.xr.hands 1.9.0.
        internal const string FID_ANDROIDXR_SUPPORT  = "com.unity.openxr.feature.androidxr-support";
        internal const string FID_AXR_SESSION        = "com.unity.openxr.feature.arfoundation-androidxr-session";
        internal const string FID_AXR_OCCLUSION      = "com.unity.openxr.feature.arfoundation-androidxr-occlusion";
        internal const string FID_AXR_CAMERA         = "com.unity.openxr.feature.arfoundation-androidxr-camera";
        internal const string FID_AXR_ANCHOR         = "com.unity.openxr.feature.arfoundation-androidxr-anchor";
        internal const string FID_AXR_MESH           = "com.unity.openxr.feature.arfoundation-androidxr-scene-meshing";
        internal const string FID_HAND_TRACKING      = "com.unity.openxr.feature.input.handtrackingsubsystem";
        internal const string FID_HAND_INTERACTION   = "com.unity.openxr.feature.input.handinteraction";
        internal const string FID_OCULUS_TOUCH       = "com.unity.openxr.feature.input.oculustouch";
        internal const string FID_FOVEATION          = "com.unity.openxr.feature.foveatedrendering";
        internal const string FID_COMPOSITION_LAYERS = "com.unity.openxr.feature.compositionlayers";

        // Feature id -> full type name, used when an id lookup misses.
        static readonly Dictionary<string, string> k_FeatureTypeNames = new()
        {
            [FID_ANDROIDXR_SUPPORT]  = "UnityEngine.XR.OpenXR.Features.Android.AndroidXRSupportFeature",
            [FID_AXR_SESSION]        = "UnityEngine.XR.OpenXR.Features.Android.ARSessionFeature",
            [FID_AXR_OCCLUSION]      = "UnityEngine.XR.OpenXR.Features.Android.AROcclusionFeature",
            [FID_AXR_CAMERA]         = "UnityEngine.XR.OpenXR.Features.Android.ARCameraFeature",
            [FID_AXR_ANCHOR]         = "UnityEngine.XR.OpenXR.Features.Android.ARAnchorFeature",
            [FID_AXR_MESH]           = "UnityEngine.XR.OpenXR.Features.Android.ARMeshFeature",
            [FID_HAND_TRACKING]      = "UnityEngine.XR.Hands.OpenXR.HandTracking",
            [FID_HAND_INTERACTION]   = "UnityEngine.XR.OpenXR.Features.Interactions.HandInteractionProfile",
            [FID_OCULUS_TOUCH]       = "UnityEngine.XR.OpenXR.Features.Interactions.OculusTouchControllerProfile",
            [FID_FOVEATION]          = "UnityEngine.XR.OpenXR.Features.FoveatedRenderingFeature",
            [FID_COMPOSITION_LAYERS] = "UnityEngine.XR.OpenXR.Features.CompositionLayers.OpenXRCompositionLayersFeature",
        };

        // Meta Quest features that must stay off on Android. Explicit ids
        // for the ones shipped inside com.unity.xr.openxr itself (they stay
        // registered after com.unity.xr.meta-openxr is removed), plus id
        // prefixes that cover every com.unity.xr.meta-openxr feature in case
        // that package is still installed.
        static readonly HashSet<string> k_MetaQuestFeatureIds = new(StringComparer.OrdinalIgnoreCase)
        {
            "com.unity.openxr.feature.metaquest",
            "com.unity.openxr.feature.oculusquest",
            "com.unity.openxr.feature.input.metaquestplus",
            "com.unity.openxr.feature.input.metaquestpro",
            "com.unity.openxr.feature.input.metahandtrackingaim",
            "com.unity.openxr.feature.metahandmeshdata",
            "com.unity.openxr.feature.spacewarp",
        };
        static readonly string[] k_MetaQuestFeatureIdPrefixes =
        {
            "com.unity.openxr.feature.arfoundation-meta-",
            "com.unity.openxr.feature.meta-",
            "MetaOpenXR-",
        };

        const string OPENXR_LOADER_TYPE = "UnityEngine.XR.OpenXR.OpenXRLoader";

        // Written only when the project still has Unity's placeholder id.
        internal const string DEFAULT_APP_ID = "com.genesis.roomscan.galaxyxr";

        // Android XR's own validation floor is API 24 in androidxr-openxr
        // 1.2.0 (AndroidXRProjectValidationRules.k_MinSupportedSdkVersion);
        // the 1.3.1 changelog raises it to 25 on Unity 6.3+. 29 clears both;
        // the headset runs API 34.
        const int MIN_SDK = 29;

        // Android applicationIdentifier rules (per Android Studio docs):
        //   * at least two dot-separated segments
        //   * each segment starts with a letter
        //   * all characters are [a-zA-Z0-9_]
        // We deliberately accept uppercase letters — Pascal-cased segments
        // such as `com.MyCompany.MyGame` are perfectly legal app-ids even
        // though all-lowercase is more idiomatic on Android.
        static readonly Regex APPID_RE =
            new(@"^[a-zA-Z][a-zA-Z0-9_]*(\.[a-zA-Z][a-zA-Z0-9_]*)+$", RegexOptions.Compiled);

        public static IReadOnlyList<VRCheck> AllChecks { get; } = BuildChecks();

        // -- Public API ---------------------------------------------------

        /// <summary>
        /// Makes sure the OpenXR settings hold an instance of every feature
        /// the checks read. Only touches the settings asset when something is
        /// missing, so the wizard can call it once per session cheaply.
        /// </summary>
        public static void Audit()
        {
            // OpenXR creates feature instances lazily in RefreshFeatures, so
            // a brand-new project (or one that just installed
            // com.unity.xr.androidxr-openxr) has none until it runs.
            // Standalone is needed too: Android XR's lifecycle feature looks
            // itself up in both groups whenever one of its AR features is
            // toggled. Refreshing an already-complete asset is skipped: older
            // OpenXR builds left orphan duplicate feature sub-assets when it
            // ran repeatedly.
            RefreshFeaturesIfMissing(BuildTargetGroup.Android, k_RequiredAndroidFeatureIds);
            RefreshFeaturesIfMissing(BuildTargetGroup.Standalone, k_RequiredStandaloneFeatureIds);
        }

        // Features whose absence means the OpenXR settings asset has not
        // caught up with the installed packages yet.
        static readonly string[] k_RequiredAndroidFeatureIds =
        {
            FID_ANDROIDXR_SUPPORT, FID_AXR_SESSION, FID_AXR_OCCLUSION, FID_AXR_CAMERA, FID_AXR_ANCHOR,
            FID_HAND_TRACKING, FID_HAND_INTERACTION, FID_OCULUS_TOUCH,
        };
        static readonly string[] k_RequiredStandaloneFeatureIds = { FID_AXR_SESSION };

        static void RefreshFeaturesIfMissing(BuildTargetGroup group, string[] featureIds)
        {
            bool needed = OpenXRSettings.GetSettingsForBuildTargetGroup(group) == null
                          || featureIds.Any(id => FindFeature(group, id) == null);
            if (!needed) return;
            try
            {
                FeatureHelpers.RefreshFeatures(group);
            }
            catch (Exception ex)
            {
                // A features array that still points at sub-assets of an
                // uninstalled package (com.unity.xr.meta-openxr) can throw
                // here; FixAll purges those first and retries.
                Debug.LogWarning($"[VR Bootstrap] OpenXR RefreshFeatures({group}) threw {ex.GetType().Name}: {ex.Message}. " +
                                 "Run Fix All (it removes stale feature entries first).");
            }
        }

        /// <summary>
        /// Removes OpenXR feature sub-assets whose script no longer exists
        /// (the Meta Quest features after com.unity.xr.meta-openxr is
        /// uninstalled) and the dangling entries they leave in each build
        /// target's feature list. OpenXR and Android XR editor code iterate
        /// that list without null checks, so stale entries break feature
        /// lookups and the Android XR manifest build step. Returns the number
        /// of objects / entries removed.
        /// </summary>
        internal static int PurgeMissingOpenXRFeatures()
        {
            var any = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android)
                      ?? OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
            string path = any != null ? AssetDatabase.GetAssetPath(any) : null;
            if (string.IsNullOrEmpty(path)) return 0;

            int removed = 0;
            try
            {
                removed += AssetDatabase.RemoveScriptableObjectsWithMissingScript(path);

                foreach (var settings in AssetDatabase.LoadAllAssetsAtPath(path).OfType<OpenXRSettings>())
                {
                    var so = new SerializedObject(settings);
                    var list = so.FindProperty("features");
                    if (list == null || !list.isArray) continue;
                    bool changed = false;
                    for (int i = list.arraySize - 1; i >= 0; i--)
                    {
                        if (list.GetArrayElementAtIndex(i).objectReferenceValue != null) continue;
                        int before = list.arraySize;
                        list.DeleteArrayElementAtIndex(i);
                        // Older Unity only nulls an object-reference element
                        // on the first delete.
                        if (list.arraySize == before) list.DeleteArrayElementAtIndex(i);
                        removed++;
                        changed = true;
                    }
                    if (changed)
                    {
                        so.ApplyModifiedPropertiesWithoutUndo();
                        EditorUtility.SetDirty(settings);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VR Bootstrap] Purging stale OpenXR features from {path} failed: {ex.Message}");
            }

            if (removed > 0)
                Debug.Log($"[VR Bootstrap] Removed {removed} stale OpenXR feature object(s) / entries from {path} " +
                          "(features of an uninstalled package).");
            return removed;
        }

        /// <summary>
        /// Runs Fix() on each check whose severity is at-or-above
        /// <paramref name="includeUpTo"/>. "Outstanding" runs only the
        /// hard-required ones; "Recommended" runs both tiers.
        /// </summary>
        public static Task FixAllAsync(CheckSeverity includeUpTo)
        {
            var report = FixAll(includeUpTo, continueAfterPlatformSwitch: false);
            if (report.PlatformSwitched)
                Debug.Log("[VR Bootstrap] Active platform switched to plain Android. " +
                          "Click Fix All again after the reload completes to apply the rest.");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Synchronous core of <see cref="FixAllAsync"/>. The build-target /
        /// platform switch runs first: interactively it returns right after
        /// issuing it (the domain reload would cut the loop short), while
        /// batch callers pass <paramref name="continueAfterPlatformSwitch"/>
        /// and apply the rest in the same run, then run again to verify.
        /// </summary>
        public static VRFixReport FixAll(CheckSeverity includeUpTo, bool continueAfterPlatformSwitch)
        {
            var report = new VRFixReport();
            if (Application.isPlaying)
            {
                Debug.LogError("[VR Bootstrap] Cannot fix while Play mode is active.");
                return report;
            }

            PurgeMissingOpenXRFeatures();
            Audit();

            if (!RoomScanSetupWizard.IsActivePlatformPlainAndroid())
            {
                Debug.Log($"[VR Bootstrap] Switching active platform {RoomScanSetupWizard.DescribeActivePlatform()} → plain Android.");
                report.PlatformSwitched = RoomScanSetupWizard.TryActivatePlainAndroidPlatform();
                if (report.PlatformSwitched && !continueAfterPlatformSwitch)
                    return report;
                Audit();
            }

            foreach (var check in AllChecks)
            {
                if (check.Severity == CheckSeverity.Recommended &&
                    includeUpTo == CheckSeverity.Outstanding)
                    continue;

                if (SafeIsOk(check)) continue;

                // The platform switch already ran (once) above.
                if (IsPlatformCheck(check) || check.Fix == null)
                {
                    report.Skipped++;
                    report.StillFailing.Add(check);
                    continue;
                }

                try
                {
                    check.Fix();
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[VR Bootstrap] Fix failed for {check.Id}: {ex.Message}\n{ex.StackTrace}");
                }

                if (SafeIsOk(check))
                {
                    report.Fixed++;
                    Debug.Log($"[VR Bootstrap] Fixed: {check.Id} ({check.Label})");
                }
                else
                {
                    report.StillFailing.Add(check);
                    Debug.LogWarning($"[VR Bootstrap] Still failing after fix: {check.Id} ({check.Label}) — " +
                                     $"current: {SafeCurrent(check)}, target: {check.TargetValue}");
                }
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[VR Bootstrap] Done. fixed={report.Fixed}, still-failing={report.StillFailing.Count}, " +
                      $"skipped(no-fix)={report.Skipped}.");
            return report;
        }

        const string CHECK_BUILD_TARGET = "android.buildtarget.active";
        const string CHECK_PLAIN_PLATFORM = "android.platform.plain";

        static bool IsPlatformCheck(VRCheck check) =>
            check.Id == CHECK_BUILD_TARGET || check.Id == CHECK_PLAIN_PLATFORM;

        internal static bool SafeIsOk(VRCheck check)
        {
            try { return check.IsOk(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[VR Bootstrap] check '{check.Id}' threw {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        internal static string SafeCurrent(VRCheck check)
        {
            try { return check.CurrentValue?.Invoke() ?? "(null)"; }
            catch (Exception ex) { return $"<error: {ex.GetType().Name}>"; }
        }

        // -- Check registry ----------------------------------------------

        static List<VRCheck> BuildChecks()
        {
            var list = new List<VRCheck>
            {
                // === Outstanding ===
                // Active build target must be Android — the entire OpenXR
                // feature configuration is meaningless until you switch.
                // Switching triggers a domain reload, so FixAll special-cases
                // this pair and runs the switch first.
                new VRCheck {
                    Id = CHECK_BUILD_TARGET,
                    Label = "Active build target = Android",
                    Severity = CheckSeverity.Outstanding,
                    Group = BuildTargetGroup.Android,
                    CurrentValue = () => EditorUserBuildSettings.activeBuildTarget.ToString(),
                    TargetValue = "Android",
                    IsOk = () => EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android,
                    Fix = () => RoomScanSetupWizard.TryActivatePlainAndroidPlatform(),
                },

                new VRCheck {
                    Id = CHECK_PLAIN_PLATFORM,
                    Label = "Active platform = plain Android (no derived platform or custom build profile)",
                    Severity = CheckSeverity.Outstanding,
                    Group = BuildTargetGroup.Android,
                    CurrentValue = RoomScanSetupWizard.DescribeActivePlatform,
                    TargetValue = "Android (plain)",
                    IsOk = RoomScanSetupWizard.IsActivePlatformPlainAndroid,
                    Fix = () => RoomScanSetupWizard.TryActivatePlainAndroidPlatform(),
                },

                MakeXRLoaderCheck(BuildTargetGroup.Android, CheckSeverity.Outstanding,
                    id: "android.xr.loader.openxr",
                    label: "Android: OpenXR loader assigned"),

                new VRCheck {
                    Id = "android.xr.loader.openxr-only",
                    Label = "Android: OpenXR is the only XR loader (one provider per build target)",
                    Severity = CheckSeverity.Outstanding,
                    Group = BuildTargetGroup.Android,
                    CurrentValue = () => DescribeLoaders(BuildTargetGroup.Android),
                    TargetValue = "OpenXRLoader only",
                    IsOk = () => OtherLoaders(BuildTargetGroup.Android).Count == 0,
                    Fix = () => RemoveOtherLoaders(BuildTargetGroup.Android),
                },

                new VRCheck {
                    Id = "android.xr.initonstart",
                    Label = "Android: Initialize XR on Startup enabled",
                    Severity = CheckSeverity.Outstanding,
                    Group = BuildTargetGroup.Android,
                    CurrentValue = () => GetXRGeneralSettings(BuildTargetGroup.Android)?.InitManagerOnStart.ToString()
                                         ?? "(no XR settings)",
                    TargetValue = "True",
                    IsOk = () => GetXRGeneralSettings(BuildTargetGroup.Android)?.InitManagerOnStart == true,
                    Fix = () => {
                        var s = GetXRGeneralSettings(BuildTargetGroup.Android);
                        if (s == null)
                        {
                            EnsureOpenXRLoader(BuildTargetGroup.Android);
                            s = GetXRGeneralSettings(BuildTargetGroup.Android);
                        }
                        if (s == null) return;
                        s.InitManagerOnStart = true;
                        EditorUtility.SetDirty(s);
                    },
                },

                // Meta off before Android XR on, so the two AR provider sets
                // are never enabled together.
                new VRCheck {
                    Id = "android.openxr.metaquest.off",
                    Label = "Android: every Meta Quest OpenXR feature disabled (Quest support, Meta AR providers, Touch Plus/Pro, SpaceWarp)",
                    Severity = CheckSeverity.Outstanding,
                    Group = BuildTargetGroup.Android,
                    CurrentValue = () => {
                        var on = EnabledMetaQuestFeatures(BuildTargetGroup.Android);
                        return on.Count == 0 ? "(none enabled)" : string.Join(", ", on.Select(DescribeFeatureName));
                    },
                    TargetValue = "none enabled",
                    IsOk = () => EnabledMetaQuestFeatures(BuildTargetGroup.Android).Count == 0,
                    Fix = () => {
                        foreach (var f in EnabledMetaQuestFeatures(BuildTargetGroup.Android))
                            SetFeatureEnabled(f, false);
                    },
                },

                MakeOpenXRFeatureCheck(BuildTargetGroup.Android, CheckSeverity.Outstanding,
                    id: "android.openxr.androidxr-support",
                    label: "Android: Android XR Support feature enabled",
                    featureId: FID_ANDROIDXR_SUPPORT),

                MakeOpenXRFeatureCheck(BuildTargetGroup.Android, CheckSeverity.Outstanding,
                    id: "android.openxr.session",
                    label: "Android: Android XR: AR Session enabled (required by every AR feature)",
                    featureId: FID_AXR_SESSION),

                MakeOpenXRFeatureCheck(BuildTargetGroup.Android, CheckSeverity.Outstanding,
                    id: "android.openxr.occlusion",
                    label: "Android: Android XR: AR Occlusion enabled (environment depth for the scan)",
                    featureId: FID_AXR_OCCLUSION),

                MakeOpenXRFeatureCheck(BuildTargetGroup.Android, CheckSeverity.Outstanding,
                    id: "android.openxr.passthrough",
                    label: "Android: Android XR: AR Camera enabled (passthrough)",
                    featureId: FID_AXR_CAMERA),

                MakeOpenXRFeatureCheck(BuildTargetGroup.Android, CheckSeverity.Outstanding,
                    id: "android.openxr.anchor",
                    label: "Android: Android XR: AR Anchor enabled (spatial anchors + persistence)",
                    featureId: FID_AXR_ANCHOR),

                new VRCheck {
                    Id = "android.openxr.armesh.off",
                    Label = "Android: Android XR: AR Mesh (scene meshing) disabled — the scan builds its own mesh",
                    Severity = CheckSeverity.Outstanding,
                    Group = BuildTargetGroup.Android,
                    CurrentValue = () => DescribeFeature(BuildTargetGroup.Android, FID_AXR_MESH),
                    TargetValue = "disabled (or not installed)",
                    IsOk = () => !IsFeatureEnabled(BuildTargetGroup.Android, FID_AXR_MESH),
                    Fix = () => SetFeatureEnabled(BuildTargetGroup.Android, FID_AXR_MESH, false),
                },

                MakeOpenXRFeatureCheck(BuildTargetGroup.Android, CheckSeverity.Outstanding,
                    id: "android.openxr.handtracking",
                    label: "Android: Hand Tracking Subsystem enabled (XR Hands joints, HAND_TRACKING)",
                    featureId: FID_HAND_TRACKING),

                MakeOpenXRFeatureCheck(BuildTargetGroup.Android, CheckSeverity.Outstanding,
                    id: "android.openxr.handinteraction",
                    label: "Android: Hand Interaction Profile enabled (pinch / aim without controllers)",
                    featureId: FID_HAND_INTERACTION),

                MakeOpenXRFeatureCheck(BuildTargetGroup.Android, CheckSeverity.Outstanding,
                    id: "android.openxr.touchprofile",
                    label: "Android: Oculus Touch Controller Profile enabled (Android XR's profile for Galaxy XR controllers)",
                    featureId: FID_OCULUS_TOUCH),

                MakeRenderModeCheck(),
                MakeDepthSubmissionCheck(),

                MakeScriptingBackendCheck(),
                MakeArm64Check(),
                MakeMinSdkCheck(),
                MakeTargetSdkCheck(),
                MakeVulkanOnlyCheck(),
                MakeTextureCompressionAstcCheck(),
                MakeGameActivityCheck(),
                MakeResizeableActivityCheck(),
                MakeRunInBackgroundCheck(),

                new VRCheck {
                    Id = "android.player.appid.set",
                    Label = "Android: applicationIdentifier is a valid, non-placeholder id",
                    Severity = CheckSeverity.Outstanding,
                    Group = BuildTargetGroup.Android,
                    CurrentValue = () => CurrentAppId() is { Length: > 0 } id ? id : "(unset)",
                    TargetValue = $"valid Android app-id; placeholder ids become {DEFAULT_APP_ID}",
                    IsOk = () => {
                        var id = CurrentAppId();
                        return APPID_RE.IsMatch(id) && !IsPlaceholderAppId(id);
                    },
                    Fix = () => {
                        var id = CurrentAppId();
                        if (IsPlaceholderAppId(id))
                            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, DEFAULT_APP_ID);
                        else
                            Debug.LogWarning($"[VR Bootstrap] applicationIdentifier '{id}' is not a valid Android id. " +
                                             "Fix it by hand in Player Settings > Android > Other Settings.");
                    },
                },

                // === Recommended ===
                MakeOpenXRFeatureCheck(BuildTargetGroup.Android, CheckSeverity.Recommended,
                    id: "android.openxr.foveation",
                    label: "Android: Foveated Rendering enabled",
                    featureId: FID_FOVEATION),

                // androidxr-openxr pulls in com.unity.xr.compositionlayers;
                // OpenXR's validator warns while its support feature is off.
                MakeOpenXRFeatureCheck(BuildTargetGroup.Android, CheckSeverity.Recommended,
                    id: "android.openxr.compositionlayers",
                    label: "Android: Composition Layers Support enabled",
                    featureId: FID_COMPOSITION_LAYERS),
            };

            return list;
        }

        // -- Region: PlayerSettings checks --------------------------------
        #region PlayerSettings

        static VRCheck MakeScriptingBackendCheck() => new()
        {
            Id = "android.player.scripting.il2cpp",
            Label = "Android: scripting backend = IL2CPP",
            Severity = CheckSeverity.Outstanding,
            Group = BuildTargetGroup.Android,
            CurrentValue = () => PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android).ToString(),
            TargetValue = "IL2CPP",
            IsOk = () => PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android)
                         == ScriptingImplementation.IL2CPP,
            Fix = () => PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android,
                         ScriptingImplementation.IL2CPP),
        };

        static VRCheck MakeArm64Check() => new()
        {
            Id = "android.player.arch.arm64",
            Label = "Android: target architectures = ARM64 only",
            Severity = CheckSeverity.Outstanding,
            Group = BuildTargetGroup.Android,
            CurrentValue = () => PlayerSettings.Android.targetArchitectures.ToString(),
            TargetValue = "ARM64",
            IsOk = () => PlayerSettings.Android.targetArchitectures == AndroidArchitecture.ARM64,
            Fix = () => PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64,
        };

        static VRCheck MakeMinSdkCheck() => new()
        {
            Id = "android.player.minsdk",
            Label = $"Android: min SDK >= {MIN_SDK} (Android XR floor is 24-25; Galaxy XR runs API 34)",
            Severity = CheckSeverity.Outstanding,
            Group = BuildTargetGroup.Android,
            CurrentValue = () => ((int)PlayerSettings.Android.minSdkVersion).ToString(),
            TargetValue = $">= {MIN_SDK}",
            IsOk = () => (int)PlayerSettings.Android.minSdkVersion >= MIN_SDK,
            Fix = () => PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)MIN_SDK,
        };

        static VRCheck MakeTargetSdkCheck() => new()
        {
            Id = "android.player.targetsdk",
            Label = "Android: target SDK = Auto (or >= 34, the Galaxy XR OS level)",
            Severity = CheckSeverity.Outstanding,
            Group = BuildTargetGroup.Android,
            CurrentValue = () => {
                var t = PlayerSettings.Android.targetSdkVersion;
                return t == AndroidSdkVersions.AndroidApiLevelAuto ? "Auto" : ((int)t).ToString();
            },
            TargetValue = "Auto (highest installed), or >= 34",
            IsOk = () => {
                var t = PlayerSettings.Android.targetSdkVersion;
                return t == AndroidSdkVersions.AndroidApiLevelAuto || (int)t >= 34;
            },
            Fix = () => PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto,
        };

        static VRCheck MakeVulkanOnlyCheck() => new()
        {
            Id = "android.player.gfxapi.vulkan",
            Label = "Android: graphics API = Vulkan only (auto APIs disabled)",
            Severity = CheckSeverity.Outstanding,
            Group = BuildTargetGroup.Android,
            CurrentValue = () => {
                var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
                var prefix = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android) ? "Auto: " : "";
                return prefix + (apis.Length == 0 ? "(none)" : string.Join(",", apis));
            },
            TargetValue = "[Vulkan] only, automatic=false",
            IsOk = () => {
                if (PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android)) return false;
                var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
                return apis.Length == 1 && apis[0] == GraphicsDeviceType.Vulkan;
            },
            Fix = () => {
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,
                    new[] { GraphicsDeviceType.Vulkan });
            },
        };

        // Galaxy XR's Adreno GPU (Snapdragon XR2+ Gen 2) decodes ASTC in
        // hardware; ETC2 falls back to a slower path and inflates VRAM.
        static VRCheck MakeTextureCompressionAstcCheck() => new()
        {
            Id = "android.player.texcompression.astc",
            Label = "Android: texture compression = ASTC only",
            Severity = CheckSeverity.Outstanding,
            Group = BuildTargetGroup.Android,
            CurrentValue = () =>
            {
                var fmts = PlayerSettings.Android.textureCompressionFormats;
                return fmts == null || fmts.Length == 0
                    ? "(none)"
                    : string.Join(",", fmts);
            },
            TargetValue = "[ASTC] only",
            IsOk = () =>
            {
                var fmts = PlayerSettings.Android.textureCompressionFormats;
                return fmts != null && fmts.Length == 1
                       && fmts[0] == TextureCompressionFormat.ASTC;
            },
            Fix = () => PlayerSettings.Android.textureCompressionFormats =
                        new[] { TextureCompressionFormat.ASTC },
        };

        // Android XR's validator treats any other entry point as an error.
        static VRCheck MakeGameActivityCheck() => new()
        {
            Id = "android.player.entry.gameactivity",
            Label = "Android: application entry point = GameActivity (Android XR requirement)",
            Severity = CheckSeverity.Outstanding,
            Group = BuildTargetGroup.Android,
            CurrentValue = () => PlayerSettings.Android.applicationEntry.ToString(),
            TargetValue = "GameActivity",
            IsOk = () => PlayerSettings.Android.applicationEntry == AndroidApplicationEntry.GameActivity,
            Fix = () => PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.GameActivity,
        };

        // Without it the runtime cannot draw pop-ups such as the permission
        // dialogs the scan depends on.
        static VRCheck MakeResizeableActivityCheck() => new()
        {
            Id = "android.player.resizeable",
            Label = "Android: resizeable activity on (system permission dialogs)",
            Severity = CheckSeverity.Outstanding,
            Group = BuildTargetGroup.Android,
            CurrentValue = () => ResizeableActivity.ToString(),
            TargetValue = "True",
            IsOk = () => ResizeableActivity,
            Fix = () => ResizeableActivity = true,
        };

        static bool ResizeableActivity
        {
#if UNITY_6000_0_4_OR_NEWER
            get => PlayerSettings.Android.resizeableActivity;
            set => PlayerSettings.Android.resizeableActivity = value;
#else
            get => PlayerSettings.Android.resizableWindow;
            set => PlayerSettings.Android.resizableWindow = value;
#endif
        }

        // Android XR docs: required with the Input System TrackedPoseDriver,
        // otherwise the view head-locks when the app loses focus to a
        // system dialog.
        static VRCheck MakeRunInBackgroundCheck() => new()
        {
            Id = "player.runinbackground",
            Label = "Player: Run In Background on (Android XR: avoids head-lock behind system dialogs)",
            Severity = CheckSeverity.Outstanding,
            Group = BuildTargetGroup.Android,
            CurrentValue = () => PlayerSettings.runInBackground.ToString(),
            TargetValue = "True",
            IsOk = () => PlayerSettings.runInBackground,
            Fix = () => PlayerSettings.runInBackground = true,
        };

        static string CurrentAppId() =>
            PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android) ?? "";

        /// <summary>
        /// Empty, or one of the ids Unity fills in on its own:
        /// com.DefaultCompany.&lt;Product&gt; (derived from the default company
        /// name), the template's com.Company.ProductName, or the legacy
        /// com.unity3d.player.
        /// </summary>
        static bool IsPlaceholderAppId(string id) =>
            string.IsNullOrEmpty(id)
            || id.StartsWith("com.DefaultCompany.", StringComparison.Ordinal)
            || id == "com.Company.ProductName"
            || id == "com.unity3d.player";

        #endregion

        // -- Region: OpenXR render settings -------------------------------
        #region OpenXRRender

        static OpenXRSettings AndroidOpenXRSettings() =>
            OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);

        static VRCheck MakeRenderModeCheck() => new()
        {
            Id = "android.openxr.rendermode.spi",
            Label = "Android: OpenXR render mode = Single Pass Instanced (multiview)",
            Severity = CheckSeverity.Outstanding,
            Group = BuildTargetGroup.Android,
            CurrentValue = () => AndroidOpenXRSettings()?.renderMode.ToString() ?? "(no OpenXR settings)",
            TargetValue = "SinglePassInstanced",
            IsOk = () => AndroidOpenXRSettings()?.renderMode == OpenXRSettings.RenderMode.SinglePassInstanced,
            Fix = () => {
                var s = AndroidOpenXRSettings();
                if (s == null) return;
                s.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
                EditorUtility.SetDirty(s);
            },
        };

        static VRCheck MakeDepthSubmissionCheck() => new()
        {
            Id = "android.openxr.depthsubmission.none",
            Label = "Android: OpenXR depth submission = None",
            Severity = CheckSeverity.Outstanding,
            Group = BuildTargetGroup.Android,
            CurrentValue = () => AndroidOpenXRSettings()?.depthSubmissionMode.ToString() ?? "(no OpenXR settings)",
            TargetValue = "None",
            IsOk = () => AndroidOpenXRSettings()?.depthSubmissionMode == OpenXRSettings.DepthSubmissionMode.None,
            Fix = () => {
                var s = AndroidOpenXRSettings();
                if (s == null) return;
                s.depthSubmissionMode = OpenXRSettings.DepthSubmissionMode.None;
                EditorUtility.SetDirty(s);
            },
        };

        #endregion

        // -- Region: XR Plug-in Management loaders ------------------------
        #region XRLoaders

        static VRCheck MakeXRLoaderCheck(
            BuildTargetGroup group, CheckSeverity sev, string id, string label) => new()
        {
            Id = id,
            Label = label,
            Severity = sev,
            Group = group,
            CurrentValue = () => DescribeLoaders(group),
            TargetValue = "OpenXRLoader",
            IsOk = () => GetXRManager(group)?.activeLoaders?.Any(l => l != null
                && l.GetType().FullName == OPENXR_LOADER_TYPE) == true,
            Fix = () => EnsureOpenXRLoader(group),
        };

        static string DescribeLoaders(BuildTargetGroup group)
        {
            var mgr = GetXRManager(group);
            if (mgr == null) return "(no XR settings)";
            var loaders = mgr.activeLoaders;
            if (loaders == null || loaders.Count == 0) return "(none)";
            return string.Join(",", loaders.Where(l => l != null).Select(l => l.GetType().Name));
        }

        static List<string> OtherLoaders(BuildTargetGroup group)
        {
            var loaders = GetXRManager(group)?.activeLoaders;
            if (loaders == null) return new List<string>();
            return loaders.Where(l => l != null && l.GetType().FullName != OPENXR_LOADER_TYPE)
                          .Select(l => l.GetType().FullName)
                          .ToList();
        }

        static void RemoveOtherLoaders(BuildTargetGroup group)
        {
            var mgr = GetXRManager(group);
            if (mgr == null) return;
            foreach (var typeName in OtherLoaders(group))
            {
                bool removed = XRPackageMetadataStore.RemoveLoader(mgr, typeName, group);
                Debug.Log($"[VR Bootstrap] Remove XR loader {typeName} from {group}: {(removed ? "ok" : "failed")}");
            }
        }

        static XRManagerSettings GetXRManager(BuildTargetGroup group)
        {
            var settings = GetXRGeneralSettings(group);
            return settings == null ? null : settings.AssignedSettings;
        }

        static XRGeneralSettings GetXRGeneralSettings(BuildTargetGroup group)
        {
            var perTarget = GetOrCreateXRPerBuildTarget();
            return perTarget == null ? null : perTarget.SettingsForBuildTarget(group);
        }

        // XRGeneralSettingsPerBuildTarget.GetOrCreate is internal — reflect.
        static XRGeneralSettingsPerBuildTarget _cachedPerTarget;
        static XRGeneralSettingsPerBuildTarget GetOrCreateXRPerBuildTarget()
        {
            // If something already created the asset (e.g. user opened the XR
            // Plug-in Management settings page) we'll find it via the existing
            // EditorBuildSettings config object first.
            if (EditorBuildSettings.TryGetConfigObject<XRGeneralSettingsPerBuildTarget>(
                    XRGeneralSettings.k_SettingsKey, out var existing) && existing != null)
            {
                _cachedPerTarget = existing;
                return existing;
            }

            if (_cachedPerTarget != null) return _cachedPerTarget;

            // Reflective access to the internal GetOrCreate so we don't need
            // to vendor a copy of its asset-creation logic.
            var t = typeof(XRGeneralSettingsPerBuildTarget);
            var m = t.GetMethod("GetOrCreate", BindingFlags.Static | BindingFlags.NonPublic);
            if (m == null)
            {
                Debug.LogError("[VR Bootstrap] XRGeneralSettingsPerBuildTarget.GetOrCreate not found via reflection — XR Plug-in Management package may have changed.");
                return null;
            }
            _cachedPerTarget = (XRGeneralSettingsPerBuildTarget)m.Invoke(null, null);
            return _cachedPerTarget;
        }

        static void EnsureOpenXRLoader(BuildTargetGroup group)
        {
            var perTarget = GetOrCreateXRPerBuildTarget();
            if (perTarget == null) return;

            if (!perTarget.HasManagerSettingsForBuildTarget(group))
                perTarget.CreateDefaultManagerSettingsForBuildTarget(group);

            var mgr = perTarget.SettingsForBuildTarget(group)?.AssignedSettings;
            if (mgr == null)
            {
                Debug.LogError($"[VR Bootstrap] Could not create XRManagerSettings for {group}.");
                return;
            }

            if (!XRPackageMetadataStore.AssignLoader(mgr, OPENXR_LOADER_TYPE, group))
            {
                Debug.LogWarning($"[VR Bootstrap] AssignLoader returned false for OpenXR on {group} (already present?)");
            }
        }

        #endregion

        // -- Region: OpenXR features --------------------------------------
        #region OpenXRFeatures

        static VRCheck MakeOpenXRFeatureCheck(
            BuildTargetGroup group, CheckSeverity sev,
            string id, string label, string featureId) => new()
        {
            Id = id,
            Label = label,
            Severity = sev,
            Group = group,
            CurrentValue = () => DescribeFeature(group, featureId),
            TargetValue = "enabled",
            IsOk = () => IsFeatureEnabled(group, featureId),
            Fix = () => SetFeatureEnabled(group, featureId, true),
        };

        /// <summary>
        /// Finds a feature by id, then by type name. Null when the package
        /// providing it is not installed (or its settings asset is missing).
        /// </summary>
        internal static OpenXRFeature FindFeature(BuildTargetGroup group, string featureId)
        {
            try
            {
                var f = FeatureHelpers.GetFeatureWithIdForBuildTarget(group, featureId);
                if (f != null) return f;
            }
            catch (NullReferenceException)
            {
                // Stale entry in the features list (see
                // PurgeMissingOpenXRFeatures); fall through to the scan below,
                // which skips nulls.
            }

            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            if (settings == null) return null;
            k_FeatureTypeNames.TryGetValue(featureId, out var typeName);
            foreach (var candidate in settings.GetFeatures())
            {
                if (candidate == null) continue;
                if (string.Equals(FeatureIdOf(candidate), featureId, StringComparison.OrdinalIgnoreCase)
                    || (typeName != null && candidate.GetType().FullName == typeName))
                    return candidate;
            }
            return null;
        }

        static string DescribeFeature(BuildTargetGroup group, string featureId)
        {
            var f = FindFeature(group, featureId);
            if (f == null) return "(feature not found — is com.unity.xr.androidxr-openxr installed?)";
            return f.enabled ? "enabled" : "disabled";
        }

        internal static bool IsFeatureEnabled(BuildTargetGroup group, string featureId)
        {
            var f = FindFeature(group, featureId);
            return f != null && f.enabled;
        }

        internal static bool SetFeatureEnabled(BuildTargetGroup group, string featureId, bool enabled)
        {
            var f = FindFeature(group, featureId);
            if (f == null)
            {
                // Materialize the settings asset / feature instance, then retry.
                FeatureHelpers.RefreshFeatures(group);
                f = FindFeature(group, featureId);
            }
            if (f == null)
            {
                if (enabled)
                    Debug.LogWarning($"[VR Bootstrap] OpenXR feature '{featureId}' not registered for {group} — package providing it may not be installed.");
                // A feature that does not exist is as good as disabled.
                return !enabled;
            }
            return SetFeatureEnabled(f, enabled);
        }

        static bool SetFeatureEnabled(OpenXRFeature f, bool enabled)
        {
            if (f.enabled != enabled)
            {
                f.enabled = enabled;
                EditorUtility.SetDirty(f);
            }
            if (f.enabled != enabled)
            {
                // OpenXRFeature.enabled silently refuses to disable a feature
                // that an enabled OpenXR feature group requires.
                Debug.LogWarning($"[VR Bootstrap] OpenXR feature '{DescribeFeatureName(f)}' refused enabled={enabled}. " +
                                 "An enabled OpenXR feature group probably requires it: untick that group in " +
                                 "Project Settings > XR Plug-in Management > OpenXR > Android.");
                return false;
            }
            return true;
        }

        // Attribute lookups are cached: the wizard re-runs every check on its
        // 0.8 s heartbeat.
        static readonly Dictionary<Type, string> s_FeatureIdByType = new();

        static string FeatureIdOf(OpenXRFeature f)
        {
            var type = f.GetType();
            if (!s_FeatureIdByType.TryGetValue(type, out var id))
            {
                id = type.GetCustomAttribute<OpenXRFeatureAttribute>(true)?.FeatureId ?? "";
                s_FeatureIdByType[type] = id;
            }
            return id;
        }

        static string DescribeFeatureName(OpenXRFeature f)
        {
            var attr = f.GetType().GetCustomAttribute<OpenXRFeatureAttribute>(true);
            return string.IsNullOrEmpty(attr?.UiName) ? f.GetType().Name : attr.UiName;
        }

        static bool IsMetaQuestFeatureId(string featureId)
        {
            if (string.IsNullOrEmpty(featureId)) return false;
            if (k_MetaQuestFeatureIds.Contains(featureId)) return true;
            foreach (var prefix in k_MetaQuestFeatureIdPrefixes)
                if (featureId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static List<OpenXRFeature> EnabledMetaQuestFeatures(BuildTargetGroup group)
        {
            var result = new List<OpenXRFeature>();
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            if (settings == null) return result;
            foreach (var f in settings.GetFeatures())
                if (f != null && f.enabled && IsMetaQuestFeatureId(FeatureIdOf(f)))
                    result.Add(f);
            return result;
        }

        #endregion

    }
}
