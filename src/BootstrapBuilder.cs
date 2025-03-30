// Copyright Bastian Eicher et al.
// Licensed under the GNU Lesser Public License

using System.ComponentModel;
using System.Net;
using Mono.Cecil;
using NanoByte.Common;
using NanoByte.Common.Collections;
using NanoByte.Common.Net;
using NanoByte.Common.Storage;
using NanoByte.Common.Streams;
using NanoByte.Common.Tasks;
using Vestris.ResourceLib;
using ZeroInstall.Bootstrap.Builder.Properties;

namespace ZeroInstall.Bootstrap.Builder;

/// <summary>
/// Builds a customized Zero Install bootstrapper for running or integrating a specific feed.
/// </summary>
/// <param name="handler">A callback object used when the user needs to be asked questions or informed about download and IO tasks.</param>
public class BootstrapBuilder(ITaskHandler handler) : IDisposable
{
    private readonly TemporaryFile _tempFile = new("0bootstrap-");

    /// <summary>
    /// Initializes the bootstrapper using a template file.
    /// </summary>
    /// <param name="template">The remote URL or local path to fetch the template from.</param>
    public void Initialize(Uri template)
    {
        if (template.IsFile)
            handler.RunTask(new ReadFile(template.LocalPath, stream => stream.CopyToFile(_tempFile)));
        else
            handler.RunTask(new DownloadFile(template, _tempFile));
    }

    /// <summary>
    /// Modifies the resources embedded in the bootstrapper.
    /// </summary>
    /// <param name="bootstrapConfig">The contents of the BootstrapConfig.ini to embed.</param>
    /// <param name="splashScreenPath">An optional path of a splash screen image to embed.</param>
    /// <param name="contentDir">An optional directory of additional content to embed.</param>
    public void ModifyEmbeddedResources(Stream bootstrapConfig, string? splashScreenPath, DirectoryInfo? contentDir)
        => handler.RunTask(new ActionTask(Resources.BuildingBootstrapper, () =>
        {
            using var assembly = AssemblyDefinition.ReadAssembly(_tempFile, parameters: new() {ReadWrite = true});
            assembly.Name.Name = Path.GetFileNameWithoutExtension(_tempFile);

            var resources = assembly.MainModule.Resources;

            void Replace(string name, Stream stream)
            {
                resources.RemoveAll(x => x.Name == name);
                resources.Add(new EmbeddedResource(name, ManifestResourceAttributes.Public, stream));
            }

            Replace("ZeroInstall.BootstrapConfig.ini", bootstrapConfig);

            using var splashScreen = splashScreenPath?.To(File.OpenRead);
            if (splashScreen != null) Replace("ZeroInstall.SplashScreen.png", splashScreen);

            contentDir?.Walk(
                fileAction: file => resources.Add(new EmbeddedResource(
                    name: "ZeroInstall.content." + WebUtility.UrlDecode(file.RelativeTo(contentDir).Replace(Path.DirectorySeparatorChar, '.')),
                    ManifestResourceAttributes.Public,
                    file.Open(FileMode.Open, FileAccess.Read, FileShare.Read))));

            assembly.Write();
        }));

    /// <summary>
    /// Replaces the metadata of the bootstrapper.
    /// </summary>
    /// <param name="appName">The name of the app the bootstrapper is for.</param>
    /// <param name="fileName">The final file name of the bootstrapper being built.</param>
    /// <exception cref="IOException"></exception>
    public void ReplaceMetadata(string appName, string fileName)
    {
        try
        {
            StringTableEntry.ConsiderPaddingForLength = true;

            var versionResource = new VersionResource();
            versionResource.LoadFrom(_tempFile);
            versionResource.Language = ResourceUtil.NEUTRALLANGID;
            versionResource.ProductVersion = "1.0.0.0";

            var stringFileInfo = (StringFileInfo)versionResource["StringFileInfo"];
            stringFileInfo["ProductName"] = appName;
            stringFileInfo["ProductVersion"] = versionResource.ProductVersion;
            stringFileInfo["FileDescription"] = $"Bootstrapper for {appName}";
            stringFileInfo["OriginalFilename"] = fileName;
            stringFileInfo["Copyright"] = "";
            stringFileInfo["Company"] = "";

            versionResource.SaveTo(_tempFile);
        }
        #region Error handling
        catch (Win32Exception ex)
        {
            // Wrap exception since only certain exception types are allowed
            throw new IOException(ex.Message, ex);
        }
        #endregion
    }

    /// <summary>
    /// Replaces the icon of the bootstrapper.
    /// </summary>
    /// <param name="iconPath">The path of the icon file to use.</param>
    public void ReplaceIcon(string iconPath)
    {
        try
        {
            new IconDirectoryResource(new(iconPath)).SaveTo(_tempFile);
        }
        #region Error handling
        catch (Win32Exception ex)
        {
            // Wrap exception since only certain exception types are allowed
            throw new IOException(ex.Message, ex);
        }
        #endregion
    }

    /// <summary>
    /// Finishes building the bootstrap file.
    /// </summary>
    /// <param name="outputPath">The path of the bootstrap file to build.</param>
    public void Complete(string outputPath)
        => FileUtils.Replace(_tempFile, outputPath);

    /// <summary>
    /// Deletes any incomplete file on disk.
    /// </summary>
    public void Dispose()
        => _tempFile.Dispose();
}
