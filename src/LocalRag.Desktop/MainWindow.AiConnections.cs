using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Desktop;

public partial class MainWindow
{
    private readonly ObservableCollection<AiEndpointSettings> _aiEndpoints = [];
    private readonly Dictionary<string, IReadOnlyList<LemonadeModel>> _aiModels = [];
    private readonly Dictionary<string, (AiEndpointSettings Endpoint, string Key)> _pendingAiKeys = [];
    private bool _aiSelectionLoading;
    private void InitializeAiConnections(AiConnectionSettings settings)
    {
        var migrated = settings.WithConnections();
        _aiSelectionLoading = true;
        foreach (var endpoint in migrated.Connections) _aiEndpoints.Add(endpoint);
        AiEndpointList.ItemsSource = ChatConnectionBox.ItemsSource = EmbeddingConnectionBox.ItemsSource = _aiEndpoints;
        ChatConnectionBox.SelectedValue = migrated.ChatConnectionId; EmbeddingConnectionBox.SelectedValue = migrated.EmbeddingConnectionId;
        AiEndpointList.SelectedIndex = 0; _aiSelectionLoading = false;
    }
    private AiConnectionSettings ReadAiSettings() => _loadedSettings.Ai with
    {
        Connections = _aiEndpoints.ToList(), ChatConnectionId = (string?)ChatConnectionBox.SelectedValue ?? "",
        EmbeddingConnectionId = (string?)EmbeddingConnectionBox.SelectedValue ?? "",
        ChatModel = ChatModelBox.Text.Trim(), EmbeddingModel = EmbeddingModelBox.Text.Trim()
    };
    private void AddAiEndpoint(object sender, RoutedEventArgs e) => EditAiEndpoint(null);
    private void EditAiEndpoint(object sender, RoutedEventArgs e) { if (AiEndpointList.SelectedItem is AiEndpointSettings endpoint) EditAiEndpoint(endpoint); }
    private void EditAiEndpoint(AiEndpointSettings? existing)
    {
        if (!_aiTask.IsCompleted || _shuttingDown) return;
        var editor = new AiEndpointWindow(existing ?? new()) { Owner = this };
        if (editor.ShowDialog() != true) return;
        var result = editor.Result!;
        var chat = ChatConnectionBox.SelectedValue; var embedding = EmbeddingConnectionBox.SelectedValue;
        _aiSelectionLoading = true;
        if (existing is null) _aiEndpoints.Add(result); else _aiEndpoints[_aiEndpoints.IndexOf(existing)] = result;
        ChatConnectionBox.SelectedValue = chat ?? result.Id; EmbeddingConnectionBox.SelectedValue = embedding ?? result.Id;
        AiEndpointList.SelectedItem = result; _aiSelectionLoading = false;
        if (editor.ApiKey is not null) _pendingAiKeys[result.Id] = (result, editor.ApiKey);
        else if (_pendingAiKeys.TryGetValue(result.Id, out var key) && !SameKeyTarget(key.Endpoint, result)) _pendingAiKeys.Remove(result.Id);
        _aiModels.Remove(result.Id); RefreshSelectedAiModels();
        SettingsStatus.Text = "接続先を変更しました。モデル一覧を更新し、「設定を保存」で反映してください。";
    }
    private void RemoveAiEndpoint(object sender, RoutedEventArgs e)
    {
        if (!_aiTask.IsCompleted || AiEndpointList.SelectedItem is not AiEndpointSettings endpoint) return;
        if (_aiEndpoints.Count == 1 || Equals(ChatConnectionBox.SelectedValue, endpoint.Id) || Equals(EmbeddingConnectionBox.SelectedValue, endpoint.Id))
        { SettingsStatus.Text = "使用中の接続先は削除できません。先に言語モデル・Embeddingモデルの接続先を切り替えてください。"; return; }
        _aiEndpoints.Remove(endpoint); _pendingAiKeys.Remove(endpoint.Id); _aiModels.Remove(endpoint.Id);
        SettingsStatus.Text = "接続先を削除しました。「設定を保存」で反映します。起動済みのサーバーは終了時の設定に従います。";
    }
    private void AiConnectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_aiSelectionLoading || ChatModelBox is null || EmbeddingModelBox is null) return;
        if (ReferenceEquals(sender, ChatConnectionBox)) ChatModelBox.Text = "";
        else EmbeddingModelBox.Text = "";
        RefreshSelectedAiModels();
    }
    private void RefreshSelectedAiModels()
    {
        FillModels(ChatModelBox, _aiModels.GetValueOrDefault((string?)ChatConnectionBox.SelectedValue ?? "") ?? [], "chat");
        FillModels(EmbeddingModelBox, _aiModels.GetValueOrDefault((string?)EmbeddingConnectionBox.SelectedValue ?? "") ?? [], "embeddings");
    }
    private static bool SameKeyTarget(AiEndpointSettings a, AiEndpointSettings b) => a.Id == b.Id && a.Provider == b.Provider && a.GetBaseUri() == b.GetBaseUri();
    private Task<string?> ReadAiKeyAsync(AiEndpointSettings endpoint) => _pendingAiKeys.TryGetValue(endpoint.Id, out var pending) && SameKeyTarget(endpoint, pending.Endpoint)
        ? Task.FromResult<string?>(pending.Key) : new AiCredentialStore(_dataDirectory).LoadAsync(endpoint, _lifetime.Token);
    private async Task SaveAiCredentialsAsync(AiConnectionSettings settings)
    {
        var store = new AiCredentialStore(_dataDirectory);
        foreach (var endpoint in settings.Endpoints())
            if (_pendingAiKeys.TryGetValue(endpoint.Id, out var pending) && SameKeyTarget(endpoint, pending.Endpoint))
                await store.SaveAsync(endpoint, pending.Key, _lifetime.Token);
        _pendingAiKeys.Clear();
    }
}
