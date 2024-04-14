// Copyright Bastian Eicher et al.
// Licensed under the GNU Lesser Public License

using System.Net;
using System.Security;
using NanoByte.Common.Net;
using NanoByte.Common.Tasks;
using NDesk.Options;
using ZeroInstall.Bootstrap.Builder;

NetUtils.ApplyProxy();

using var handler = new AnsiCliTaskHandler();

try
{
    new BootstrapCommand(args is [] ? ["--help"] : args, handler).Execute();
    return (int)ExitCode.OK;
}
#region Error handling
catch (OperationCanceledException)
{
    return (int)ExitCode.UserCanceled;
}
catch (Exception ex) when (ex is ArgumentException or FormatException or OptionException)
{
    handler.Error(ex);
    return (int)ExitCode.InvalidArguments;
}
catch (WebException ex)
{
    handler.Error(ex);
    return (int)ExitCode.WebError;
}
catch (InvalidDataException ex)
{
    handler.Error(ex);
    return (int)ExitCode.InvalidData;
}
catch (IOException ex)
{
    handler.Error(ex);
    return (int)ExitCode.IOError;
}
catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
{
    handler.Error(ex);
    return (int)ExitCode.AccessDenied;
}
catch (NotSupportedException ex)
{
    handler.Error(ex);
    return (int)ExitCode.NotSupported;
}
#endregion
