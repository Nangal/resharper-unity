using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace Plugins.Editor.JetBrains
{
  public class RiderAssetPostprocessor : AssetPostprocessor
  {
    public static void OnGeneratedCSProjectFiles()
    {
      if (!RiderPlugin.Enabled)
        return;
      var currentDirectory = Directory.GetCurrentDirectory();
      var projectFiles = Directory.GetFiles(currentDirectory, "*.csproj");

      foreach (var file in projectFiles)
      {
        UpgradeProjectFile(file);
      }

      var slnFile = Directory.GetFiles(currentDirectory, "*.sln").First();
      RiderPlugin.Log(RiderPlugin.LoggingLevel.Verbose, string.Format("Post-processing {0}", slnFile));
      string content = File.ReadAllText(slnFile);
      var lines = content.Split(new[] {Environment.NewLine}, StringSplitOptions.RemoveEmptyEntries);
      var sb = new StringBuilder();
      foreach (var line in lines)
      {
        if (line.StartsWith("Project("))
        {
          MatchCollection mc = Regex.Matches(line, "\"([^\"]*)\"");
          //RiderPlugin.Log(RiderPlugin.LoggingLevel.Info, "mc[1]: "+mc[1].Value);
          //RiderPlugin.Log(RiderPlugin.LoggingLevel.Info, "mc[2]: "+mc[2].Value);
          var to = GetFileNameWithoutExtension(mc[2].Value.Substring(1, mc[2].Value.Length-1)); // remove quotes
          //RiderPlugin.Log(RiderPlugin.LoggingLevel.Info, "to:" + to);
          //RiderPlugin.Log(RiderPlugin.LoggingLevel.Info, line);
          var newLine = line.Substring(0, mc[1].Index + 1) + to + line.Substring(mc[1].Index + mc[1].Value.Length - 1);
          sb.Append(newLine);
          //RiderPlugin.Log(RiderPlugin.LoggingLevel.Info, newLine);
        }
        else
        {
          sb.Append(line);
        }
        sb.Append(Environment.NewLine);
      }
      File.WriteAllText(slnFile, sb.ToString());
    }

    private static void AddPackagesConfig(string currentDirectory)
    {
      if (!Directory.GetFiles(currentDirectory, "packages.config").Any())
      {
        XmlDocument doc = new XmlDocument();
        XmlNode docNode = doc.CreateXmlDeclaration("1.0", "utf-8", null);
        doc.AppendChild(docNode);

        XmlNode packages = doc.CreateElement("packages");
        doc.AppendChild(packages);

        XmlNode packagElement = doc.CreateElement("package");
        var idAttribute = doc.CreateAttribute("id");
        idAttribute.Value = "NUnit";
        packagElement.Attributes.Append(idAttribute);
        var version = doc.CreateAttribute("version");
        version.Value = "3.5.0";
        packagElement.Attributes.Append(version);
        var targetFramework = doc.CreateAttribute("targetFramework");
        targetFramework.Value = "net35";
        packagElement.Attributes.Append(targetFramework);
        packages.AppendChild(packagElement);
        doc.Save(Path.Combine(currentDirectory, "packages.config"));
      }
    }

    private static string GetFileNameWithoutExtension(string path)
    {
      if (string.IsNullOrEmpty(path))
        return null;
      int length;
      return (length = path.LastIndexOf('.')) == -1 ? path : path.Substring(0, length);
    }

    private static void UpgradeProjectFile(string projectFile)
    {
      RiderPlugin.Log(RiderPlugin.LoggingLevel.Verbose, string.Format("Post-processing {0}", projectFile));
      var doc = XDocument.Load(projectFile);
      var projectContentElement = doc.Root;
      XNamespace xmlns = projectContentElement.Name.NamespaceName; // do not use var

      FixTargetFrameworkVersion(projectContentElement, xmlns);
      SetLangVersion(projectContentElement, xmlns);
      ChangeNunitReference(new FileInfo(projectFile).DirectoryName, projectContentElement, xmlns);
      
#if !UNITY_2017_1_OR_NEWER // Unity 2017.1 and later has this features by itself 
      SetManuallyDefinedComilingSettings(projectFile, projectContentElement, xmlns);
      SetXCodeDllReference("UnityEditor.iOS.Extensions.Xcode.dll", xmlns, projectContentElement);
      SetXCodeDllReference("UnityEditor.iOS.Extensions.Common.dll", xmlns, projectContentElement);
#endif
      doc.Save(projectFile);
    }

    private static void ChangeNunitReference(string baseDir, XElement projectContentElement, XNamespace xmlns)
    {
      var el = projectContentElement
        .Elements(xmlns+"ItemGroup")
        .Elements(xmlns+"Reference")
        .FirstOrDefault(a => a.Attribute("Include").Value=="nunit.framework");
      if (el != null)
      {
        AddPackagesConfig(baseDir);
        var hintPath = el.Elements(xmlns + "HintPath").FirstOrDefault();
        if (hintPath != null)
        {
          hintPath.Value = @"packages\NUnit.3.5.0\lib\net35\nunit.framework.dll";
        }
      }
    }

#if !UNITY_2017_1_OR_NEWER  // Unity 2017.1 and later has this features by itself
    private const string UNITY_PLAYER_PROJECT_NAME = "Assembly-CSharp.csproj";
    private const string UNITY_EDITOR_PROJECT_NAME = "Assembly-CSharp-Editor.csproj";
    private const string UNITY_UNSAFE_KEYWORD = "-unsafe";
    private const string UNITY_DEFINE_KEYWORD = "-define:";
    private const string PLAYER_PROJECT_MANUAL_CONFIG_RELATIVE_FILE_PATH = "smcs.rsp";
    private static readonly string  PLAYER_PROJECT_MANUAL_CONFIG_ABSOLUTE_FILE_PATH = Path.Combine(UnityEngine.Application.dataPath, PLAYER_PROJECT_MANUAL_CONFIG_RELATIVE_FILE_PATH);
    private const string EDITOR_PROJECT_MANUAL_CONFIG_RELATIVE_FILE_PATH = "gmcs.rsp";
    private static readonly string  EDITOR_PROJECT_MANUAL_CONFIG_ABSOLUTE_FILE_PATH = Path.Combine(UnityEngine.Application.dataPath, EDITOR_PROJECT_MANUAL_CONFIG_RELATIVE_FILE_PATH);

    private static void SetManuallyDefinedComilingSettings(string projectFile, XElement projectContentElement, XNamespace xmlns)
    {
      string configPath;

      if (IsPlayerProjectFile(projectFile))
        configPath = PLAYER_PROJECT_MANUAL_CONFIG_ABSOLUTE_FILE_PATH;
      else if (IsEditorProjectFile(projectFile))
        configPath = EDITOR_PROJECT_MANUAL_CONFIG_ABSOLUTE_FILE_PATH;
      else
        configPath = null;

      if(!string.IsNullOrEmpty(configPath))
        ApplyManualCompilingSettings(configPath
          , projectContentElement
          , xmlns);
    }

    private static void ApplyManualCompilingSettings(string configFilePath, XElement projectContentElement, XNamespace xmlns)
    {
      if (File.Exists(configFilePath))
      {
        var configText = File.ReadAllText(configFilePath);
        if (configText.Contains(UNITY_UNSAFE_KEYWORD))
        {
          // Add AllowUnsafeBlocks to the .csproj. Unity doesn't generate it (although VSTU does).
          // Strictly necessary to compile unsafe code
          ApplyAllowUnsafeBlocks(projectContentElement, xmlns);
        }
        if (configText.Contains(UNITY_DEFINE_KEYWORD))
        {
          // defines could be
          // 1) -define:DEFINE1,DEFINE2
          // 2) -define:DEFINE1;DEFINE2
          // 3) -define:DEFINE1 -define:DEFINE2
          // 4) -define:DEFINE1,DEFINE2;DEFINE3
          // tested on "-define:DEF1;DEF2 -define:DEF3,DEF4;DEFFFF \n -define:DEF5"
          // result: DEF1, DEF2, DEF3, DEF4, DEFFFF, DEF5

          var definesList = new List<string>();
          var compileFlags = configText.Split(' ', '\n');
          foreach (var flag in compileFlags)
          {
            var f = flag.Trim();
            if (f.Contains(UNITY_DEFINE_KEYWORD))
            {
              var defineEndPos = f.IndexOf(UNITY_DEFINE_KEYWORD) + UNITY_DEFINE_KEYWORD.Length;
              var definesSubString = f.Substring(defineEndPos,f.Length - defineEndPos);
              definesSubString = definesSubString.Replace(";", ",");
              definesList.AddRange(definesSubString.Split(','));
            }
          }

          ApplyCustomDefines(definesList.ToArray(), projectContentElement, xmlns);
        }
      }
    }

    private static void ApplyCustomDefines(string[] customDefines, XElement projectContentElement, XNamespace xmlns)
    {
      var definesString = string.Join(";", customDefines);

      var DefineConstants = projectContentElement
        .Elements(xmlns+"PropertyGroup")
        .Elements(xmlns+"DefineConstants")
        .FirstOrDefault(definesConsts=> !string.IsNullOrEmpty(definesConsts.Value));

      if (DefineConstants != null)
      {
        DefineConstants.SetValue(DefineConstants.Value + ";" + definesString);
      }
    }

    private static void ApplyAllowUnsafeBlocks(XElement projectContentElement, XNamespace xmlns)
    {
      projectContentElement.AddFirst(
        new XElement(xmlns + "PropertyGroup", new XElement(xmlns + "AllowUnsafeBlocks", true)));
    }

    private static bool IsPlayerProjectFile(string projectFile)
    {
      return Path.GetFileName(projectFile) == UNITY_PLAYER_PROJECT_NAME;
    }

    private static bool IsEditorProjectFile(string projectFile)
    {
      return Path.GetFileName(projectFile) == UNITY_EDITOR_PROJECT_NAME;
    }
  
    private static void SetXCodeDllReference(string name, XNamespace xmlns, XElement projectContentElement)
    {
      string unityAppBaseFolder = Path.GetDirectoryName(EditorApplication.applicationPath);

      var xcodeDllPath = Path.Combine(unityAppBaseFolder, Path.Combine("Data/PlaybackEngines/iOSSupport", name));
      if (!File.Exists(xcodeDllPath))
        xcodeDllPath = Path.Combine(unityAppBaseFolder, Path.Combine("PlaybackEngines/iOSSupport", name));

      if (File.Exists(xcodeDllPath))
      {
        var itemGroup = new XElement(xmlns + "ItemGroup");
        var reference = new XElement(xmlns + "Reference");
        reference.Add(new XAttribute("Include", Path.GetFileNameWithoutExtension(xcodeDllPath)));
        reference.Add(new XElement(xmlns + "HintPath", xcodeDllPath));
        itemGroup.Add(reference);
        projectContentElement.Add(itemGroup);
      }
    }
#endif
    // Helps resolve System.Linq under mono 4 - RIDER-573
    private static void FixTargetFrameworkVersion(XElement projectElement, XNamespace xmlns)
    {
      var targetFrameworkVersion = projectElement.Elements(xmlns + "PropertyGroup")
        .Elements(xmlns + "TargetFrameworkVersion")
        .FirstOrDefault(); // Processing csproj files, which are not Unity-generated #56
      if (targetFrameworkVersion != null)
      {
        var version = new Version(targetFrameworkVersion.Value.Substring(1));
        if (RiderPlugin.TargetFrameworkVersion45)
        {
          if (version < new Version(4, 5))
            targetFrameworkVersion.SetValue("v4.5");
        }
        else
        {
          targetFrameworkVersion.SetValue("v3.5");
        }
      }
    }

    private static void SetLangVersion(XElement projectElement, XNamespace xmlns)
    {
      // Add LangVersion to the .csproj. Unity doesn't generate it (although VSTU does).
      // Not strictly necessary, as the Unity plugin for Rider will work it out, but setting
      // it makes Rider work if it's not installed.
      var langVersion = projectElement.Elements(xmlns + "PropertyGroup").Elements(xmlns + "LangVersion")
        .FirstOrDefault(); // Processing csproj files, which are not Unity-generated #56
      if (langVersion != null)
      {
        langVersion.SetValue(GetLanguageLevel());
      }
      else
      {
        projectElement.AddFirst(new XElement(xmlns + "PropertyGroup",
          new XElement(xmlns + "LangVersion", GetLanguageLevel())));
      }
    }

    private static string GetLanguageLevel()
    {
      // https://bitbucket.org/alexzzzz/unity-c-5.0-and-6.0-integration/src
      if (Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "CSharp70Support")))
        return "7";
      if (Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "CSharp60Support")))
        return "6";

      // Unity 5.5 supports C# 6, but only when targeting .NET 4.6. The enum doesn't exist pre Unity 5.5
#if !UNITY_5_6_OR_NEWER
      if ((int)PlayerSettings.apiCompatibilityLevel >= 3)
      #else
      if ((int) PlayerSettings.GetApiCompatibilityLevel(EditorUserBuildSettings.selectedBuildTargetGroup) >= 3)
#endif
        return "6";

      return "4";
    }
  }
}