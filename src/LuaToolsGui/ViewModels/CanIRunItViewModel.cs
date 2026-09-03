using CommunityToolkit.Mvvm.ComponentModel;
using LuaToolsGui.Models;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

public partial class CanIRunItViewModel(
    CanIRunItService service,
    OpenRouterKeyStore keyStore) : ObservableObject
{
    private string? _sessionKey;
    private CancellationTokenSource? _cancellation;

    [ObservableProperty] private long _appId;
    [ObservableProperty] private string _gameName = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _needsApiKey;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private CanIRunItAnalysis? _analysis;

    public async Task LoadAsync(long appId, string gameName)
    {
        AppId = appId;
        GameName = gameName;
        Analysis = null;
        Error = null;
        _sessionKey = keyStore.Load();
        if (string.IsNullOrWhiteSpace(_sessionKey))
        {
            NeedsApiKey = true;
            return;
        }
        await AnalyzeAsync(_sessionKey, remember: false);
    }

    public Task SubmitKeyAsync(string key, bool remember) => AnalyzeAsync(key, remember);

    public Task RetryAsync()
    {
        if (string.IsNullOrWhiteSpace(_sessionKey))
        {
            NeedsApiKey = true;
            return Task.CompletedTask;
        }
        return AnalyzeAsync(_sessionKey, remember: false);
    }

    public void Cancel() => _cancellation?.Cancel();

    private async Task AnalyzeAsync(string key, bool remember)
    {
        key = key.Trim();
        if (key.Length == 0)
        {
            Error = Resources.Strings.CanIRunIt_KeyRequired;
            NeedsApiKey = true;
            return;
        }

        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        _sessionKey = key;
        IsLoading = true;
        NeedsApiKey = false;
        Error = null;
        Analysis = null;
        try
        {
            Analysis = await service.AnalyzeAsync(AppId, GameName, key, _cancellation.Token);
            if (remember && !keyStore.IsManagedByEnvironment) keyStore.Save(key);
        }
        catch (OperationCanceledException) { }
        catch (CanIRunItException ex)
        {
            Error = FriendlyError(ex);
            if (ex.Code is "invalid_api_key" or "missing_api_key")
            {
                if (!keyStore.IsManagedByEnvironment) keyStore.Clear();
                NeedsApiKey = true;
            }
        }
        catch (Exception ex)
        {
            Error = string.Format(Resources.Strings.CanIRunIt_Error_Generic, ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string FriendlyError(CanIRunItException exception) => exception.Code switch
    {
        "invalid_api_key" or "missing_api_key" => Resources.Strings.CanIRunIt_Error_InvalidKey,
        "free_tier_limited" => Resources.Strings.CanIRunIt_Error_RateLimit,
        "requirements_unavailable" => Resources.Strings.CanIRunIt_Error_NoRequirements,
        "hardware_detection_failed" => Resources.Strings.CanIRunIt_Error_Hardware,
        "backend_missing" => Resources.Strings.CanIRunIt_Error_BackendMissing,
        "steam_unavailable" or "steam_request_failed" => Resources.Strings.CanIRunIt_Error_Steam,
        "openrouter_unavailable" => Resources.Strings.CanIRunIt_Error_OpenRouter,
        _ => string.Format(Resources.Strings.CanIRunIt_Error_Generic, exception.Message),
    };
}
