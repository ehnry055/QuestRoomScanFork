// Non-interactive Samsung Galaxy XR setup, for batch mode / CI.
//
//   Unity -batchmode -projectPath <project> -logFile - -quit \
//         -executeMethod Genesis.RoomScan.Editor.RoomScanSetupWizard.ApplyGalaxyXRSetupBatch
//
// Runs, without dialogs, everything the wizard's ANDROID XR PROJECT section,
// Game-Ready preset and Debug preset do, on GALAXY_SCENE_PATH:
//   platform (plain Android) -> stale OpenXR features purged -> URP (HDR
//   off, post-processing off) -> Outstanding project checks (OpenXR loader,
//   Android XR features on, Meta features off, IL2CPP / ARM64 / Vulkan /
//   Run In Background / app id / min SDK ...) -> Android manifest additions
//   (CAMERA + cleartext HTTP) -> scene (XR Origin Floor, MainCamera with an
//   enabled ARCameraManager + disabled AROcclusionManager, AR Session, the
//   game-ready scan modules, debug HUD + input, shader wiring) -> scene
//   saved and first in Build Settings -> OpenXR and Android XR project
//   validation.
//
// Each step ends with one line (no stack trace), and every Outstanding
// project check gets its own "project/<check id>" line:
//   [RoomScan Batch] PASS <step>: <detail>
//   [RoomScan Batch] FAIL <step>: <detail>
// WARN / INFO lines (Recommended checks, individual validation issues,
// xatlas) never fail the run. The last line is
//   [RoomScan Batch] RESULT: PASS|FAIL (...)
//
// Call it TWICE, in two separate Unity processes. Everything is idempotent,
// but some changes only take effect after a script reload that a batch
// process cannot wait for inside one -executeMethod call: a platform switch
// recompiles scripts for Android, and OpenXR features of a just-installed
// package register on the next domain load. The first run applies; the
// second run must report RESULT: PASS (and exit 0).
//
// ApplyGalaxyXRSetupBatch exits the editor with code 1 on FAIL in batch
// mode. Callers that do more work afterwards (GalaxyXRBuild.Configure in the
// test project) use TryApplyGalaxyXRSetup, which only returns the result.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEditor.XR.OpenXR;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Interaction.Toolkit.UI;
using UnityEngine.XR.OpenXR.Features;
using Object = UnityEngine.Object;

namespace Genesis.RoomScan.Editor
{
    public partial class RoomScanSetupWizard
    {
        /// <summary>Scene the batch setup configures (created empty if missing).</summary>
        public const string GALAXY_SCENE_PATH = "Assets/Scenes/RoomScan.unity";

        const string BATCH_TAG = "[RoomScan Batch]";

        /// <summary>
        /// Batch-mode entry point (<c>-executeMethod</c>). Runs
        /// <see cref="TryApplyGalaxyXRSetup"/> and, in batch mode, exits the
        /// editor with code 1 when any step failed. Call it twice in two
        /// separate Unity processes; see the file header.
        /// </summary>
        public static void ApplyGalaxyXRSetupBatch()
        {
            bool ok = TryApplyGalaxyXRSetup();
            if (!ok && Application.isBatchMode)
            {
                AssetDatabase.SaveAssets();
                EditorApplication.Exit(1);
            }
        }

        [MenuItem("RoomScan/Apply Galaxy XR Setup (all steps)")]
        static void ApplyGalaxyXRSetupMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            bool ok = TryApplyGalaxyXRSetup();
            EditorUtility.DisplayDialog("Galaxy XR Setup",
                ok ? "All steps passed. See the Console for the per-step log."
                   : "Some steps failed. Search the Console for \"" + BATCH_TAG + " FAIL\".",
                "OK");
        }

        /// <summary>
        /// Non-interactive Galaxy XR setup (see the file header). Opens
        /// <see cref="GALAXY_SCENE_PATH"/>, applies every project, URP,
        /// OpenXR and scene step, saves the scene and assets, and adds the
        /// scene to Build Settings. Leaves that scene open. Idempotent; safe
        /// to call repeatedly. Returns true when no step failed.
        /// </summary>
        public static bool TryApplyGalaxyXRSetup()
        {
            var log = new BatchLog();
            log.Info("start", $"Unity {Application.unityVersion}, batchMode={Application.isBatchMode}, " +
                              $"scene {GALAXY_SCENE_PATH}");

            if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                log.Step("editor-state", false, "Play mode is active; exit Play mode first.");
                return log.Finish();
            }

            RoomScanSetupWizard wizard = null;
            try
            {
                // -- Platform ------------------------------------------------
                log.Run("platform", () =>
                {
                    if (IsActivePlatformPlainAndroid()) return (true, "Android (plain)");
                    string from = DescribeActivePlatform();
                    bool issued = TryActivatePlainAndroidPlatform();
                    if (issued) log.NeedsSecondRun = true;
                    bool now = IsActivePlatformPlainAndroid();
                    return (now, issued
                        ? $"switched {from} -> {DescribeActivePlatform()}; scripts recompile for Android, run again"
                        : $"still {from}; no API could switch it. Select Android in File > Build Profiles.");
                });

                // -- Scene ---------------------------------------------------
                var scene = default(UnityEngine.SceneManagement.Scene);
                if (!log.Run("scene-open", () =>
                    {
                        scene = OpenOrCreateGalaxyScene(out string how);
                        return (scene.IsValid() && scene.isLoaded, how);
                    }))
                    return log.Finish();

                // -- OpenXR settings asset hygiene ---------------------------
                log.Run("openxr-stale-features", () =>
                {
                    int n = VRProjectBootstrap.PurgeMissingOpenXRFeatures();
                    return (true, n == 0 ? "none" : $"removed {n} (uninstalled package's features)");
                });

                // -- URP -----------------------------------------------------
                log.Run("urp", () =>
                {
                    var asset = EnsureURPSetup();
                    bool ok = IsURPConfiguredForAndroidXR(asset);
                    return (ok, asset == null
                        ? "no URP asset could be created"
                        : $"{AssetDatabase.GetAssetPath(asset)}: HDR {(asset.supportsHDR ? "ON" : "off")}, " +
                          $"post-processing {(UrpPostProcessingOff(asset) ? "off" : "ON")}, " +
                          $"all quality levels {(AllQualityLevelsUseUrp(asset) ? "assigned" : "NOT assigned")}");
                });

                // -- Project checks (VRProjectBootstrap) ----------------------
                log.Run("project-fix", () =>
                {
                    var report = VRProjectBootstrap.FixAll(CheckSeverity.Outstanding, continueAfterPlatformSwitch: true);
                    if (report.PlatformSwitched) log.NeedsSecondRun = true;
                    return (true, $"fixed {report.Fixed}, still failing {report.StillFailing.Count}");
                });

                // The IL2CPP check passes when the backend merely reads as
                // IL2CPP, which can be Unity's default rather than a stored
                // value; write it so ProjectSettings holds it explicitly.
                log.Run("player-il2cpp-explicit", () =>
                {
                    PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
                    return (PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android) == ScriptingImplementation.IL2CPP,
                            "Android scripting backend = IL2CPP");
                });

                foreach (var check in VRProjectBootstrap.AllChecks)
                {
                    bool ok = VRProjectBootstrap.SafeIsOk(check);
                    string detail = $"{check.Label} (current: {VRProjectBootstrap.SafeCurrent(check)}; target: {check.TargetValue})";
                    if (check.Severity == CheckSeverity.Outstanding) log.Step("project/" + check.Id, ok, detail);
                    else if (ok) log.Info("project/" + check.Id, detail);
                    else log.Warn("project/" + check.Id, "recommended, not applied: " + detail);
                }

                log.Run("player-allow-http", () =>
                {
                    if (PlayerSettings.insecureHttpOption == InsecureHttpOption.NotAllowed)
                        PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
                    return (PlayerSettings.insecureHttpOption != InsecureHttpOption.NotAllowed,
                            $"insecureHttpOption = {PlayerSettings.insecureHttpOption} (LAN splat server)");
                });

                log.Run("android-manifest", () =>
                {
                    EnsureAndroidXRManifest();
                    bool perms = AndroidXRManifestLibHasPermissions();
                    bool cleartext = AndroidXRManifestLibHasCleartext();
                    bool questLeft = MainManifestHasQuestEntries();
                    return (perms && cleartext && !questLeft,
                            $"{ANDROIDLIB_DIR}: CAMERA {(perms ? "declared" : "MISSING")}, cleartext " +
                            $"{(cleartext ? "on" : "MISSING")}; {MANIFEST_PATH} Quest/Horizon entries " +
                            $"{(questLeft ? "PRESENT" : "none")}. Scene-understanding and hand-tracking " +
                            "permissions come from the Android XR build step.");
                });

                // -- Scene modules (wizard instance, never shown) ------------
                wizard = CreateInstance<RoomScanSetupWizard>();
                wizard.hideFlags = HideFlags.HideAndDontSave;
                var w = wizard;
                w.Refresh();

                log.Run("scene-xr-rig", () =>
                {
                    w.EnsureXRRig();
                    w.Refresh();
                    var cam = FindXRCamera();
                    return (w._cameraRig != null && cam != null,
                            cam != null ? $"XR Origin '{w._cameraRig.name}', camera '{cam.name}'" : "no XR Origin camera");
                });

                log.Run("scene-ar-session", () =>
                {
                    if (w._arSession == null) w.FixARSession();
                    var session = FindAny<ARSession>();
                    return (session != null,
                            session != null ? $"'{session.name}' (ARInputManager " +
                                              $"{(session.GetComponent<ARInputManager>() != null ? "present" : "missing")})"
                                            : "no ARSession");
                });

                log.Run("scene-occlusion", () =>
                {
                    w.Refresh();
                    if (w._arOcclusion == null) w.FixAROcclusion();
                    var cam = FindXRCamera();
                    var occl = cam != null ? cam.GetComponent<AROcclusionManager>() : null;
                    return (occl != null,
                            occl != null ? $"AROcclusionManager on '{cam.name}' (enabled={occl.enabled}; " +
                                           "DepthCapture enables it once SCENE_UNDERSTANDING_FINE is granted)"
                                         : "no AROcclusionManager on the XR camera");
                });

                log.Run("scene-modules", () =>
                {
                    w.Refresh();
                    w.AddGameReadyComponentsToRoot();
                    w.FixDebugModules();
                    w.Refresh();
                    var root = w._roomScanner != null ? w._roomScanner.gameObject : null;
                    if (root == null) return (false, "no RoomScanner in the scene");
                    var missing = new List<string>();
                    void Need<T>(bool onRoot = true) where T : Component
                    {
                        bool found = onRoot ? root.GetComponent<T>() != null : FindAny<T>() != null;
                        if (!found) missing.Add(typeof(T).Name);
                    }
                    Need<DepthCapture>(); Need<VolumeIntegrator>(); Need<MeshExtractor>();
                    Need<RoomScanPersistence>(); Need<RoomAnchorManager>(); Need<RoomScanSession>();
                    Need<PassthroughCameraProvider>(); Need<TextureRefinement>(); Need<RoomUnderstanding>();
                    Need<RoomScanInputHandler>(); Need<DepthDebugOverlay>(); Need<CameraDebugOverlay>();
                    Need<UI.DebugMenuController>(false); Need<EventSystem>(false); Need<XRUIInputModule>(false);
                    return (missing.Count == 0, missing.Count == 0
                        ? $"'{root.name}': core + game-ready + debug modules"
                        : "missing: " + string.Join(", ", missing));
                });

                // Camera last: the module steps above add components to it.
                log.Run("scene-camera", () =>
                {
                    ConfigureXRCameraForAndroidXR();
                    bool ok = IsXRCameraConfigured(out string problem);
                    return (ok, ok ? "MainCamera, SolidColor alpha 0, both eyes, ARCameraManager enabled, " +
                                     "XR Origin Floor, no URP post-processing"
                                   : problem);
                });

                log.Run("scene-shader-wiring", () =>
                {
                    w.Refresh();
                    w.FixShaderWiring();
                    w.Refresh();
                    var unwired = new List<string>();
                    if (w._depthCapture != null && !w._depthCaptureWired) unwired.Add("DepthCapture");
                    if (w._volumeIntegrator != null && !w._volumeWired) unwired.Add("VolumeIntegrator");
                    if (w._meshExtractor != null && !(w._meshMatWired && w._computeShaderWired)) unwired.Add("MeshExtractor");
                    if (w._triplanarCache != null && !w._triplanarWired) unwired.Add("TriplanarCache");
                    if (w._textureRefinement != null &&
                        !(w._refinedShaderWired && w._occlusionShaderWired && w._atlasBakeComputeWired))
                        unwired.Add("TextureRefinement");
                    if (w._roomScanner != null && !w._debugOverlayWired) unwired.Add("RoomScanner debug overlay");
                    return (unwired.Count == 0, unwired.Count == 0 ? "all present components wired"
                                                                   : "unwired: " + string.Join(", ", unwired));
                });

                log.Run("scene-save", () =>
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    bool saved = EditorSceneManager.SaveScene(scene);
                    return (saved, saved ? scene.path : "SaveScene returned false");
                });

                log.Run("build-scenes", () =>
                {
                    var list = EditorBuildSettings.scenes
                        .Where(s => s.path != GALAXY_SCENE_PATH)
                        .ToList();
                    list.Insert(0, new EditorBuildSettingsScene(GALAXY_SCENE_PATH, true));
                    EditorBuildSettings.scenes = list.ToArray();
                    var first = EditorBuildSettings.scenes.FirstOrDefault();
                    return (first != null && first.path == GALAXY_SCENE_PATH && first.enabled,
                            $"{EditorBuildSettings.scenes.Length} scene(s), first = {first?.path}");
                });

                log.Run("save-assets", () =>
                {
                    AssetDatabase.SaveAssets();
                    return (true, "saved");
                });

                // -- Validation (what the build itself will enforce) ---------
                log.Run("openxr-validation", () => CheckOpenXRValidation(log));
                log.Run("androidxr-validation", () => CheckCoreUtilsValidation(log));

                // -- Informational --------------------------------------------
                w.RefreshNativePlugins();
                if (w._xatlasAndroid) log.Info("xatlas", "libxatlas.so (Android ARM64) present");
                else log.Warn("xatlas", "libxatlas.so (Android ARM64) not built; texture refinement is " +
                                        "unavailable until the wizard's \"Build xatlas Plugin\" runs (needs the NDK). " +
                                        "The depth-only scan does not need it.");

                AssetDatabase.SaveAssets();
            }
            catch (Exception ex)
            {
                log.Step("unexpected", false, $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                if (wizard != null) Object.DestroyImmediate(wizard);
            }

            return log.Finish();
        }

        static UnityEngine.SceneManagement.Scene OpenOrCreateGalaxyScene(out string how)
        {
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (active.path == GALAXY_SCENE_PATH && active.isLoaded)
            {
                how = "already open";
                return active;
            }

            if (File.Exists(ProjectPath(GALAXY_SCENE_PATH)))
            {
                how = "opened";
                return EditorSceneManager.OpenScene(GALAXY_SCENE_PATH, OpenSceneMode.Single);
            }

            string dir = Path.GetDirectoryName(GALAXY_SCENE_PATH)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
                AssetDatabase.CreateFolder(Path.GetDirectoryName(dir), Path.GetFileName(dir));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, GALAXY_SCENE_PATH);
            how = "created (empty; the rig and modules are added below)";
            return scene;
        }

        /// <summary>
        /// OpenXR project validation for Android, as the OpenXR build step
        /// runs it (errors fail the build). Error rules with an automatic
        /// fix are fixed once, then re-checked.
        /// </summary>
        static (bool, string) CheckOpenXRValidation(BatchLog log)
        {
            var issues = new List<OpenXRFeature.ValidationRule>();
            OpenXRProjectValidation.GetCurrentValidationIssues(issues, BuildTargetGroup.Android);

            int autoFixed = 0;
            foreach (var rule in issues.Where(r => r.error && r.fixItAutomatic && r.fixIt != null).ToList())
            {
                try
                {
                    rule.fixIt();
                    autoFixed++;
                    log.Info("openxr-validation", "auto-fixed: " + rule.message);
                }
                catch (Exception ex)
                {
                    log.Warn("openxr-validation", $"auto-fix threw {ex.GetType().Name} for: {rule.message}");
                }
            }
            if (autoFixed > 0)
            {
                AssetDatabase.SaveAssets();
                OpenXRProjectValidation.GetCurrentValidationIssues(issues, BuildTargetGroup.Android);
            }

            int errors = 0;
            foreach (var rule in issues)
            {
                if (rule.error) { errors++; log.Warn("openxr-validation", "ERROR " + rule.message); }
                else log.Warn("openxr-validation", "warning " + rule.message);
            }
            return (errors == 0, $"{errors} error(s), {issues.Count - errors} warning(s)" +
                                 (autoFixed > 0 ? $", {autoFixed} auto-fixed" : ""));
        }

        /// <summary>
        /// XR Core Utils project validation for Android (where the Android XR
        /// package registers its GameActivity / resizeable / min SDK / HDR /
        /// post-processing rules). Those rules do not stop a build, so only
        /// errors in the "Android XR" category fail this step; the rest are
        /// reported as warnings. Read by reflection: the rule list is internal
        /// to Unity.XR.CoreUtils.Editor.
        /// </summary>
        static (bool, string) CheckCoreUtilsValidation(BatchLog log)
        {
            var validator = Type.GetType("Unity.XR.CoreUtils.Editor.BuildValidator, Unity.XR.CoreUtils.Editor");
            var rulesProp = validator?.GetProperty("PlatformRules", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (rulesProp?.GetValue(null) is not IDictionary byGroup)
                return (true, "XR Core Utils rule list not reachable (API changed); skipped");
            if (!byGroup.Contains(BuildTargetGroup.Android) || byGroup[BuildTargetGroup.Android] is not IEnumerable rules)
                return (true, "no Android rules registered");

            static T Get<T>(object rule, string name) =>
                rule.GetType().GetProperty(name)?.GetValue(rule) is T v ? v : default;

            bool Failing(object rule)
            {
                try
                {
                    var enabled = Get<Func<bool>>(rule, "IsRuleEnabled");
                    var check = Get<Func<bool>>(rule, "CheckPredicate");
                    return (enabled == null || enabled()) && check != null && !check();
                }
                catch { return false; }
            }

            int androidXRErrors = 0, other = 0, autoFixed = 0;
            foreach (var rule in rules.Cast<object>().ToList())
            {
                if (!Failing(rule)) continue;
                string category = Get<string>(rule, "Category") ?? "";
                string message = Get<string>(rule, "Message") ?? "(no message)";
                bool error = Get<bool>(rule, "Error");
                var fix = Get<Action>(rule, "FixIt");
                if (error && fix != null && Get<bool>(rule, "FixItAutomatic"))
                {
                    try { fix(); } catch { /* reported below if still failing */ }
                    if (!Failing(rule))
                    {
                        autoFixed++;
                        log.Info("androidxr-validation", $"auto-fixed [{category}] {message}");
                        continue;
                    }
                }

                if (error && category == "Android XR")
                {
                    androidXRErrors++;
                    log.Warn("androidxr-validation", $"ERROR [{category}] {message}");
                }
                else
                {
                    other++;
                    log.Warn("androidxr-validation", $"{(error ? "error" : "warning")} [{category}] {message}");
                }
            }
            if (autoFixed > 0) AssetDatabase.SaveAssets();
            return (androidXRErrors == 0, $"{androidXRErrors} Android XR error(s), {other} other issue(s)" +
                                          (autoFixed > 0 ? $", {autoFixed} auto-fixed" : ""));
        }

        /// <summary>One-line-per-step logger for the batch setup.</summary>
        sealed class BatchLog
        {
            readonly List<string> _failed = new();
            int _passed;

            /// <summary>A change needs a script reload before it can be
            /// verified (see the file header).</summary>
            public bool NeedsSecondRun;

            static void Write(LogType type, string line) =>
                Debug.LogFormat(type, LogOption.NoStacktrace, null, "{0}", line);

            public void Step(string step, bool ok, string detail)
            {
                if (ok) _passed++; else _failed.Add(step);
                Write(ok ? LogType.Log : LogType.Error, $"{BATCH_TAG} {(ok ? "PASS" : "FAIL")} {step}: {detail}");
            }

            public void Warn(string step, string detail) => Write(LogType.Warning, $"{BATCH_TAG} WARN {step}: {detail}");
            public void Info(string step, string detail) => Write(LogType.Log, $"{BATCH_TAG} INFO {step}: {detail}");

            /// <summary>Runs a step; an exception counts as FAIL.</summary>
            public bool Run(string step, Func<(bool ok, string detail)> body)
            {
                try
                {
                    var (ok, detail) = body();
                    Step(step, ok, detail);
                    return ok;
                }
                catch (Exception ex)
                {
                    Step(step, false, $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                    return false;
                }
            }

            public bool Finish()
            {
                bool ok = _failed.Count == 0;
                string rerun = NeedsSecondRun ? " A script reload is pending: run the setup again to verify." : "";
                Write(ok ? LogType.Log : LogType.Error,
                      $"{BATCH_TAG} RESULT: {(ok ? "PASS" : "FAIL")} ({_passed} passed, {_failed.Count} failed" +
                      (ok ? ")" : ": " + string.Join(", ", _failed) + ")") + rerun);
                return ok;
            }
        }
    }
}
