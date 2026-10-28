// Command line build script for Unity 2017.4 to 2022 (projects without Build Profiles).
// Builds the scenes ticked in File > Build Settings for one target and exits with
// code 0 on success or 1 on failure, so Jenkins can tell whether the build worked.
//
// Written in C# 4 (no string interpolation or ?.) so it compiles on Unity 2017.x
// with the legacy .NET 3.5 scripting runtime.
//
// CLI usage example:
//   Unity -batchmode -quit -projectPath . -buildTarget Linux64 -buildType Release
//         -executeMethod CommandLineBuild.Build -logFile build.log
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
#if UNITY_2018_1_OR_NEWER
using UnityEditor.Build.Reporting;
#endif

public static class CommandLineBuild
{
  /// <summary>
  /// Builds the enabled scenes for -buildTarget (Linux64, Win64 or WebGL) into Builds/{target}/{buildType}/.
  /// Optional arguments: -buildType Release|Development, -devBuild, -outputPath path.
  /// </summary>
  public static void Build()
  {
    try
    {
      Environment.ExitCode = BuildInternal() ? 0 : 1;
    }
    catch (Exception e)
    {
      Debug.LogError("[CLI Build] FAILED with exception: " + e);
      Environment.ExitCode = 1;
    }
    EditorApplication.Exit(Environment.ExitCode);
  }

  private static bool BuildInternal()
  {
    string targetName = GetCommandLineArgument("-buildTarget");
    if (string.IsNullOrEmpty(targetName))
    {
      Debug.LogError("[CLI Build] Error: '-buildTarget' argument is missing (use Linux64, Win64 or WebGL).");
      return false;
    }

    BuildTarget target;
    BuildTargetGroup group;
    if (!TryGetTarget(targetName, out target, out group))
    {
      Debug.LogError("[CLI Build] Error: unsupported -buildTarget '" + targetName + "' (use Linux64, Win64 or WebGL).");
      return false;
    }

    bool development = HasCommandLineFlag("-devBuild");
    string buildType = GetCommandLineArgument("-buildType");
    if (string.IsNullOrEmpty(buildType))
    {
      buildType = development ? "Development" : "Release";
    }
    else if (buildType.Equals("Development", StringComparison.OrdinalIgnoreCase))
    {
      development = true;
    }

    // 1. Scenes: every scene ticked in File > Build Settings
    List<string> scenes = new List<string>();
    foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
    {
      if (scene.enabled && !string.IsNullOrEmpty(scene.path))
      {
        scenes.Add(scene.path);
      }
    }
    if (scenes.Count == 0)
    {
      Debug.LogError("[CLI Build] Error: no scenes are enabled in File > Build Settings. Add your scenes there and commit ProjectSettings/EditorBuildSettings.asset.");
      return false;
    }
    Debug.Log("[CLI Build] Scenes: " + string.Join(", ", scenes.ToArray()));

    // 2. Switch platform (normally already done by Unity's own -buildTarget argument)
    if (EditorUserBuildSettings.activeBuildTarget != target)
    {
      Debug.Log("[CLI Build] Switching active build target to " + target + ".");
      if (!EditorUserBuildSettings.SwitchActiveBuildTarget(group, target))
      {
        Debug.LogError("[CLI Build] Error: could not switch to " + target + ". Is the platform module installed for this Unity version?");
        return false;
      }
    }

    // 3. Output path: Builds/{target}/{buildType}/
    string outputPath = GetCommandLineArgument("-outputPath");
    if (string.IsNullOrEmpty(outputPath))
    {
      string baseDir = Path.Combine(Path.Combine("Builds", targetName), buildType);
      outputPath = GetDefaultBinaryPath(baseDir, target, Application.productName);
    }
    string directory = target == BuildTarget.WebGL ? outputPath : Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(directory))
    {
      // Safety check: ensure the target directory is strictly a child of the actual Unity project root
      if (!IsSafePathWithinProject(directory))
      {
        Debug.LogError("[CLI Build] Error: Output directory '" + directory + "' is outside the Unity project folder. Deletion blocked for safety.");
        return false;
      }

      // Start from an empty folder, so an old player can't be mistaken for this build's output
      if (Directory.Exists(directory))
      {
        Directory.Delete(directory, true); 
      }
      Directory.CreateDirectory(directory);
    }
    Debug.Log("[CLI Build] Target: " + target + ", type: " + buildType + ", output path: '" + outputPath + "'");

    BuildPlayerOptions options = new BuildPlayerOptions();
    options.scenes = scenes.ToArray();
    options.target = target;
    options.locationPathName = outputPath;
    options.options = BuildOptions.None;
    if (development)
    {
      Debug.Log("[CLI Build] Enabling Development Build flag.");
      options.options |= BuildOptions.Development;
    }
#if UNITY_2018_1_OR_NEWER
    options.targetGroup = group;
#endif

    // 4. Use Mono where IL2CPP cannot build on this machine, then put the setting back
    ScriptingImplementation originalBackend = PlayerSettings.GetScriptingBackend(group);
    bool backendChanged = false;
    if (originalBackend == ScriptingImplementation.IL2CPP && !CanUseIl2Cpp(target))
    {
      Debug.LogWarning("[CLI Build] IL2CPP cannot build " + target + " on this machine with this Unity version. Building with Mono instead.");
      PlayerSettings.SetScriptingBackend(group, ScriptingImplementation.Mono2x);
      backendChanged = true;
    }

    try
    {
      // 5. Build and check the result
#if UNITY_2018_1_OR_NEWER
      BuildReport report = BuildPipeline.BuildPlayer(options);
      if (report.summary.result == BuildResult.Succeeded)
      {
        Debug.Log("[CLI Build] SUCCESS: " + target + " " + buildType + " build completed. Output: " + report.summary.outputPath);
        return true;
      }
      Debug.LogError("[CLI Build] FAILED: " + target + " " + buildType + " build result " + report.summary.result + " with " + report.summary.totalErrors + " error(s).");
      return false;
#else
      string error = BuildPipeline.BuildPlayer(options);
      if (string.IsNullOrEmpty(error))
      {
        Debug.Log("[CLI Build] SUCCESS: " + target + " " + buildType + " build completed. Output: " + outputPath);
        return true;
      }
      if (PlayerExists(target, outputPath))
      {
        Debug.LogWarning("[CLI Build] Unity reported '" + error + "' but the player was written. Check the errors above.");
        Debug.Log("[CLI Build] SUCCESS: " + target + " " + buildType + " build completed. Output: " + outputPath);
        return true;
      }
      Debug.LogError("[CLI Build] FAILED: " + target + " " + buildType + " build: " + error);
      return false;
#endif
    }
    finally
    {
      if (backendChanged)
      {
        PlayerSettings.SetScriptingBackend(group, originalBackend);
      }
    }
  }

  private static bool IsSafePathWithinProject(string targetDirectory)
  {
    try
    {
      // Application.dataPath gives .../ProjectFolder/Assets.
      // Its parent is the guaranteed absolute path of the Unity project root.
      string projectRoot = Directory.GetParent(Application.dataPath).FullName;
      string fullTarget = Path.GetFullPath(targetDirectory);

      // Prevent targeting the project root folder itself
      if (fullTarget.Equals(projectRoot, StringComparison.OrdinalIgnoreCase))
      {
        return false;
      }

      // Ensure proper trailing separators to prevent substring collisions 
      // (e.g., C:/MyProject vs C:/MyProjectBackup)
      string normalizedRoot = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
      string normalizedTarget = fullTarget.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

      return normalizedTarget.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
    catch (Exception)
    {
      return false;
    }
  }

  private static bool TryGetTarget(string name, out BuildTarget target, out BuildTargetGroup group)
  {
    switch (name.ToLowerInvariant())
    {
      case "win64":
      case "standalonewindows64":
        target = BuildTarget.StandaloneWindows64;
        group = BuildTargetGroup.Standalone;
        return true;
      case "linux64":
      case "standalonelinux64":
        target = BuildTarget.StandaloneLinux64;
        group = BuildTargetGroup.Standalone;
        return true;
      case "webgl":
        target = BuildTarget.WebGL;
        group = BuildTargetGroup.WebGL;
        return true;
      default:
        target = BuildTarget.NoTarget;
        group = BuildTargetGroup.Unknown;
        return false;
    }
  }

  private static bool CanUseIl2Cpp(BuildTarget target)
  {
    switch (target)
    {
      case BuildTarget.StandaloneWindows64:
        return Application.platform == RuntimePlatform.WindowsEditor;
      case BuildTarget.StandaloneLinux64:
#if UNITY_2019_3_OR_NEWER
        return Application.platform == RuntimePlatform.LinuxEditor;
#else
        return false;
#endif
      default:
        return true;
    }
  }

  private static bool PlayerExists(BuildTarget target, string outputPath)
  {
    if (target == BuildTarget.WebGL)
    {
      return File.Exists(Path.Combine(outputPath, "index.html"));
    }
    return File.Exists(outputPath);
  }

  private static string GetDefaultBinaryPath(string baseDir, BuildTarget target, string productName)
  {
    switch (target)
    {
      case BuildTarget.StandaloneWindows64:
        return Path.Combine(baseDir, productName + ".exe");
      case BuildTarget.StandaloneLinux64:
        return Path.Combine(baseDir, productName + ".x86_64");
      case BuildTarget.WebGL:
        return baseDir;
      default:
        return Path.Combine(baseDir, productName);
    }
  }

  private static string GetCommandLineArgument(string flag)
  {
    string[] args = Environment.GetCommandLineArgs();
    for (int i = 0; i < args.Length; i++)
    {
      if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
      {
        return args[i + 1];
      }
    }
    return null;
  }

  private static bool HasCommandLineFlag(string flag)
  {
    string[] args = Environment.GetCommandLineArgs();
    foreach (string arg in args)
    {
      if (arg.Equals(flag, StringComparison.OrdinalIgnoreCase))
      {
        return true;
      }
    }
    return false;
  }
}
