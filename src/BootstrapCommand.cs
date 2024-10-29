// Copyright Bastian Eicher et al.
// Licensed under the GNU Lesser Public License

using System.Globalization;
using IniParser;
using IniParser.Model;
using NanoByte.Common;
using NanoByte.Common.Info;
using NanoByte.Common.Storage;
using NanoByte.Common.Tasks;
using NDesk.Options;
using ZeroInstall.Bootstrap.Builder.Properties;
using ZeroInstall.Client;
using ZeroInstall.Model;
using ZeroInstall.Store.Configuration;
using ZeroInstall.Store.Feeds;
using ZeroInstall.Store.Icons;
using ZeroInstall.Store.Trust;

namespace ZeroInstall.Bootstrap.Builder;

/// <summary>
/// Processes command-line arguments for building a customized Zero Install bootstrapper.
/// </summary>
internal class BootstrapCommand
{
    private readonly ITaskHandler _handler;
    private readonly IFeedCache _feedCache = FeedCaches.Default(OpenPgp.Verifying());
    private readonly IIconStore _iconStore;

    /// <summary>The feed URI of the target application to bootstrap.</summary>
    private readonly FeedUri _feedUri;

    /// <summary>
    /// Parses command-line arguments.
    /// </summary>
    /// <param name="args">The command-line arguments to be parsed.</param>
    /// <param name="handler">A callback object used when the user needs to be asked questions or informed about download and IO tasks.</param>
    /// <exception cref="OperationCanceledException">The user asked to see help information, version information, etc..</exception>
    /// <exception cref="OptionException"><paramref name="args"/> contains unknown options.</exception>
    public BootstrapCommand(IEnumerable<string> args, ITaskHandler handler)
    {
        _handler = handler;
        _iconStore = new IconStore(
            Locations.GetCacheDirPath("0install.net", machineWide: false, "icons"),
            Config.LoadSafe(),
            handler);

        switch (BuildOptions().Parse(args))
        {
            case [var feedUri]:
                _feedUri = new(feedUri);
                break;

            case [var feedUri, var outputFile]: // Backward compatibility
                _feedUri = new(feedUri);
                _outputFile = outputFile;
                break;

            default:
                throw new OptionException(string.Format(Resources.WrongNumberOfArguments, "0bootstrap --help"), "");
        }
    }

    #region Options
    /// <summary>The path of the bootstrap file to build.</summary>
    private string? _outputFile;

    /// <summary>Overwrite existing files.</summary>
    private bool _force;

    /// <summary>Additional command-line arguments to pass to the application launched by the feed.</summary>
    private string? _appArgs;

    /// <summary>Command-line arguments to pass to <c>0install integrate</c>. <c>null</c> to not call '0install integrate' at all.</summary>
    private string? _integrateArgs;

    /// <summary>The URI of the catalog to replace the default catalog. Only applies if Zero Install is not already deployed.</summary>
    private FeedUri? _catalogUri;

    /// <summary>Offer the user to choose a custom path for storing implementations.</summary>
    private bool _customizableStorePath;

    /// <summary>Show the estimated disk space required (in bytes). Only works when <see cref="_customizableStorePath"/> is <c>true</c>.</summary>
    private int? _estimatedRequiredSpace;

    /// <summary>Set Zero Install configuration options. Only overrides existing config files if Zero Install is not already deployed.</summary>
    private readonly KeyDataCollection _config = new();

    /// <summary>A directory containing additional content to be embedded in the bootstrapper.</summary>
    private DirectoryInfo? _contentDir;

    /// <summary>Path or URI to the boostrap template executable.</summary>
    private Uri? _template;

    private OptionSet BuildOptions()
    {
        var options = new OptionSet
        {
            // Version information
            {
                "V|version", () => Resources.OptionVersion, _ =>
                {
                    Console.WriteLine(@"0bootstrap (.NET version) " + AppInfo.Current.Version + Environment.NewLine + AppInfo.Current.Copyright + Environment.NewLine + Resources.LicenseInfo);
                    throw new OperationCanceledException(); // Don't handle any of the other arguments
                }
            },

            {"o|output=", () => Resources.OptionOutput, x => _outputFile = x},
            {"f|force", () => Resources.OptionForce, _ => _force = true},
            {"a|app-args=", () => Resources.OptionAppArgs, x => _appArgs = x},
            {"i|integrate-args=", () => Resources.OptionIntegrateArgs, x => _integrateArgs = x},
            {"catalog-uri=", () => Resources.OptionCatalogUri, (FeedUri x) => _catalogUri = x},
            {"customizable-store-path", () => Resources.OptionCustomizableStorePath, _ => _customizableStorePath = true},
            {"estimated-required-space=", () => Resources.OptionEstimatedRequiredSpace, (int x) =>
                {
                    _customizableStorePath = true;
                    _estimatedRequiredSpace = x;
                }
            },
            {"c|config==", () => Resources.OptionConfig, (key, value) =>
                {
                    new Config().SetOption(key, value); // Ensure key-value combination is valid
                    _config[key] = value; // Store raw input to allow resetting back to default values
                }
            },
            {"content=", () => Resources.OptionContent, x => _contentDir = new(x)},
            {"template=", () => Resources.OptionTemplate, (Uri x) => _template = x}
        };

        options.Add("h|help|?", () => Resources.OptionHelp, _ =>
        {
            Console.WriteLine(Resources.DescriptionBootstrap);
            Console.WriteLine();
            Console.WriteLine(Resources.Usage);
            Console.WriteLine(@"0bootstrap [OPTIONS] FEED-URI");
            Console.WriteLine();
            Console.WriteLine(Resources.Options);
            options.WriteOptionDescriptions(Console.Out);

            // Don't handle any of the other arguments
            throw new OperationCanceledException();
        });

        return options;
    }
    #endregion

    /// <summary>
    /// Executes the commands specified by the command-line arguments.
    /// </summary>
    public void Execute()
    {
        (var feed, string? keyFingerprint) = DownloadFeed();
        _outputFile ??= feed.Name + ".exe";

        if (!_force && File.Exists(_outputFile)) throw new IOException(string.Format(Resources.FileAlreadyExists, _outputFile));

        string? icon = feed.Icons.GetIcon(Icon.MimeTypeIco)?.To(_iconStore.GetFresh);
        string? splashScreen = feed.SplashScreens.GetIcon(Icon.MimeTypePng)?.To(_iconStore.GetFresh);

        using var builder = new BootstrapBuilder(_handler);
        builder.Initialize(_template ?? GetDefaultTemplate(feed.NeedsTerminal));

        using var bootstrapConfig = BuildBootstrapConfig(feed, keyFingerprint, customSplashScreen: splashScreen != null);
        builder.ModifyEmbeddedResources(bootstrapConfig, splashScreen, _contentDir);

        if (icon != null) builder.ReplaceIcon(icon);

        builder.Complete(_outputFile);

        _handler.OutputLow(
            title: string.Format(Resources.BootstrapperFor, feed.Name),
            message: string.Format(Resources.GeneratedFile, _outputFile));
    }

    private (Feed, string? keyFingerprint) DownloadFeed()
    {
        _handler.RunTask(new ActionTask(
            string.Format(Resources.Downloading, _feedUri.ToStringRfc()),
            () => ZeroInstallClient.Detect.SelectAsync(_feedUri, refresh: true).Wait()));

        return (
            _feedCache.GetFeed(_feedUri) ?? throw new FileNotFoundException(),
            _feedCache.GetSignatures(_feedUri).OfType<ValidSignature>().FirstOrDefault()?.FormatFingerprint()
        );
    }

    private Uri GetDefaultTemplate(bool needsTerminal)
        => new(needsTerminal && _integrateArgs == null && !_customizableStorePath
                ? "https://get.0install.net/0install.exe" // CLI
                : "https://get.0install.net/zero-install.exe" // GUI
        );

    private Stream BuildBootstrapConfig(Feed feed, string? keyFingerprint, bool customSplashScreen)
    {
        var iniData = new IniData
        {
            Sections =
            {
                new("global") {Keys = _config},
                new("bootstrap")
                {
                    Keys =
                    {
                        ["key_fingerprint"] = keyFingerprint ?? "",
                        ["app_uri"] = _feedUri.ToStringRfc(),
                        ["app_name"] = feed.Name,
                        ["app_args"] = _appArgs ?? "",
                        ["integrate_args"] = _integrateArgs ?? "",
                        ["catalog_uri"] = _catalogUri?.ToStringRfc() ?? "",
                        ["show_app_name_below_splash_screen"] = (!customSplashScreen).ToString().ToLowerInvariant(),
                        ["customizable_store_path"] = _customizableStorePath.ToString().ToLowerInvariant(),
                        ["estimated_required_space"] = _estimatedRequiredSpace?.ToString(CultureInfo.InvariantCulture) ?? ""
                    }
                }
            }
        };

        var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, EncodingUtils.Utf8, bufferSize: 1024, leaveOpen: true))
            new StreamIniDataParser().WriteData(writer, iniData);
        stream.Position = 0;
        return stream;
    }
}
