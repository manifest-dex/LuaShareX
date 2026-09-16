using SteamKit2.Authentication;

namespace LuaShareX.Services;

/// <summary>
/// IAuthenticator that delegates Steam Guard code requests to async callbacks
/// wired to the WPF UI (instead of blocking the console).
/// </summary>
public sealed class DelegateAuthenticator : IAuthenticator
{
    public Func<string, bool, Task<string>>? EmailCodeFn { get; set; }
    public Func<bool, Task<string>>? DeviceCodeFn { get; set; }
    public Func<Task<bool>>? MobileConfirmationFn { get; set; }

    public Task<bool> AcceptDeviceConfirmationAsync()
        => MobileConfirmationFn?.Invoke() ?? Task.FromResult(false);

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        => DeviceCodeFn?.Invoke(previousCodeWasIncorrect) ?? Task.FromResult(string.Empty);

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        => EmailCodeFn?.Invoke(email, previousCodeWasIncorrect) ?? Task.FromResult(string.Empty);
}
