using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using LocalNote.App.Commands;
using LocalNote.App.Dialogs;
using LocalNote.App.Services;
using LocalNote.Domain.Entities;
using LocalNote.Storage.Repositories;
using LocalNote.Storage.Services;
using Microsoft.Win32;

namespace LocalNote.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly VaultService _vaultService = new();
    private readonly UserSettingsService _settings = new();
    private VaultInfo? _vault;
    private NotebookRepository? _notebookRepository;
    private SectionRepository? _sectionRepository;
    private PageRepository? _pageRepository;
    private Notebook? _selectedNotebook;
    private Section? _selectedSection;
    private Page? _selectedPage;
    private string _statusText = "准备就绪";
    private bool _isVaultOpen;
    private SessionStateService? _sessionState;
    private VaultWriteLock? _writeLock;
    private readonly Dictionary<string, string> _lastSectionByNotebook = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastPageBySection = new(StringComparer.Ordinal);
    private string? _restoreNotebookId;
    private string? _restoreSectionId;
    private string? _restorePageId;
    private string? _pageToFocusId;

    public MainViewModel()
    {
        CreateVaultCommand = new AsyncRelayCommand(CreateVaultAsync);
        OpenVaultCommand = new AsyncRelayCommand(OpenVaultAsync);
        AddNotebookCommand = new AsyncRelayCommand(AddNotebookAsync, () => IsVaultOpen);
        RenameNotebookCommand = new AsyncRelayCommand(RenameNotebookAsync, () => SelectedNotebook is not null);
        DeleteNotebookCommand = new AsyncRelayCommand(DeleteNotebookAsync, () => SelectedNotebook is not null);
        AddSectionCommand = new AsyncRelayCommand(AddSectionAsync, () => SelectedNotebook is not null);
        RenameSectionCommand = new AsyncRelayCommand(RenameSectionAsync, () => SelectedSection is not null);
        DeleteSectionCommand = new AsyncRelayCommand(DeleteSectionAsync, () => SelectedSection is not null);
        AddPageCommand = new AsyncRelayCommand(AddPageAsync, () => SelectedSection is not null);
        RenamePageCommand = new AsyncRelayCommand(RenamePageAsync, () => SelectedPage is not null);
        DeletePageCommand = new AsyncRelayCommand(DeletePageAsync, () => SelectedPage is not null);
    }

    public ObservableCollection<Notebook> Notebooks { get; } = [];
    public ObservableCollection<Section> Sections { get; } = [];
    public ObservableCollection<Page> Pages { get; } = [];

    public AsyncRelayCommand CreateVaultCommand { get; }
    public AsyncRelayCommand OpenVaultCommand { get; }
    public AsyncRelayCommand AddNotebookCommand { get; }
    public AsyncRelayCommand RenameNotebookCommand { get; }
    public AsyncRelayCommand DeleteNotebookCommand { get; }
    public AsyncRelayCommand AddSectionCommand { get; }
    public AsyncRelayCommand RenameSectionCommand { get; }
    public AsyncRelayCommand DeleteSectionCommand { get; }
    public AsyncRelayCommand AddPageCommand { get; }
    public AsyncRelayCommand RenamePageCommand { get; }
    public AsyncRelayCommand DeletePageCommand { get; }

    public SqliteDataStore? ActiveStore { get; private set; }
    public ContentObjectRepository? ContentRepository { get; private set; }
    public InkRepository? InkRepository { get; private set; }
    public SearchRepository? SearchRepository { get; private set; }
    public TrashRepository? TrashRepository { get; private set; }
    public MediaStoreService? MediaStore { get; private set; }
    public BackupService? BackupService { get; private set; }
    public VaultIntegrityService? IntegrityService { get; private set; }
    public string? VaultRoot => _vault?.RootPath;
    public event EventHandler? ActivePageChanged;
    public event EventHandler? NewPageCreated;

    public bool IsVaultOpen
    {
        get => _isVaultOpen;
        private set { if (SetProperty(ref _isVaultOpen, value)) Raise(nameof(VaultSummary)); }
    }

    public string VaultSummary => _vault is null ? "尚未打开本地仓库" : $"{_vault.Name}  ·  {_vault.RootPath}  ·  可用 {DiskSpaceGuard.GetFreeSpaceSummary(_vault.RootPath)}";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    public Notebook? SelectedNotebook
    {
        get => _selectedNotebook;
        set
        {
            if (_selectedNotebook is not null && _selectedSection is not null)
                _lastSectionByNotebook[_selectedNotebook.Id] = _selectedSection.Id;
            if (!SetProperty(ref _selectedNotebook, value)) return;
            RefreshCanExecute();
            _ = LoadSectionsAsync(value);
        }
    }

    public Section? SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (_selectedSection is not null && _selectedPage is not null)
                _lastPageBySection[_selectedSection.Id] = _selectedPage.Id;
            if (!SetProperty(ref _selectedSection, value)) return;
            if (_selectedNotebook is not null && value is not null)
                _lastSectionByNotebook[_selectedNotebook.Id] = value.Id;
            RefreshCanExecute();
            _ = LoadPagesAsync(value);
        }
    }

    public Page? SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (!SetProperty(ref _selectedPage, value)) return;
            if (_selectedSection is not null && value is not null)
                _lastPageBySection[_selectedSection.Id] = value.Id;
            RefreshCanExecute();
            ActivePageChanged?.Invoke(this, EventArgs.Empty);
            _ = PersistNavigationStateAsync();
            if (value is not null && value.Id == _pageToFocusId)
            {
                _pageToFocusId = null;
                NewPageCreated?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public async Task InitializeAsync()
    {
        var path = await _settings.GetLastVaultAsync();
        if (path is null || !Directory.Exists(path))
        {
            StatusText = "欢迎使用 LocalNote · 请创建或打开本地仓库";
            return;
        }
        try { await LoadVaultAsync(path); }
        catch
        {
            await _settings.SaveLastVaultAsync(null);
            StatusText = "上次使用的本地仓库无法打开 · 请重新选择或创建";
        }
    }

    public async Task ShutdownAsync()
    {
        if (_sessionState is not null) await _sessionState.EndAsync();
        _writeLock?.Dispose(); _writeLock = null;
    }

    public async Task RefreshNavigationAsync() => await LoadNotebooksAsync();
    public Task OpenVaultPathAsync(string path) => LoadVaultAsync(path);

    public async Task RenameSelectedPageInlineAsync(string title)
    {
        if (SelectedPage is null || _pageRepository is null) return;
        title = string.IsNullOrWhiteSpace(title) ? "无标题页" : title.Trim();
        if (title == SelectedPage.Title) return;
        var id = SelectedPage.Id;
        await _pageRepository.RenameAsync(id, title);
        await LoadPagesAsync(SelectedSection);
        SelectedPage = Pages.FirstOrDefault(x => x.Id == id);
        StatusText = "页面标题已自动保存";
    }

    public async Task<IReadOnlyList<PageDestination>> GetPageDestinationsAsync() =>
        _pageRepository is null ? Array.Empty<PageDestination>() : await _pageRepository.GetDestinationsAsync();

    public async Task ChangeSelectedSectionColorAsync(string color)
    {
        if (SelectedSection is null || _sectionRepository is null) return;
        var sectionId = SelectedSection.Id;
        await _sectionRepository.UpdateColorAsync(sectionId, color);
        SelectedSection.Color = color;
        var index = Sections.IndexOf(SelectedSection);
        if (index >= 0)
        {
            var current = SelectedSection;
            Sections[index] = new Section
            {
                Id = current.Id, NotebookId = current.NotebookId, ParentGroupId = current.ParentGroupId, Name = current.Name, Color = color,
                SortOrder = current.SortOrder, IsDeleted = current.IsDeleted, CreatedAt = current.CreatedAt, UpdatedAt = DateTimeOffset.UtcNow
            };
            SelectedSection = Sections[index];
        }
        StatusText = "分区颜色已更新";
    }

    public async Task MoveSelectedPageAsync(string targetSectionId)
    {
        if (SelectedPage is null || _pageRepository is null) return;
        var pageId = SelectedPage.Id; await _pageRepository.MoveAsync(pageId, targetSectionId);
        await LoadPagesAsync(SelectedSection); StatusText = "页面已移动";
    }

    public async Task CopySelectedPageAsync(string targetSectionId)
    {
        if (SelectedPage is null || _pageRepository is null) return;
        var copy = await _pageRepository.CopyAsync(SelectedPage.Id, targetSectionId);
        if (SelectedSection?.Id == targetSectionId)
        {
            await LoadPagesAsync(SelectedSection); SelectedPage = Pages.FirstOrDefault(x => x.Id == copy.Id);
        }
        StatusText = "页面已复制";
    }

    public async Task ToggleSelectedPagePinAsync()
    {
        if (SelectedPage is null || _pageRepository is null) return;
        var id = SelectedPage.Id; var value = !SelectedPage.IsPinned;
        await _pageRepository.SetPinnedAsync(id, value);
        await LoadPagesAsync(SelectedSection);
        SelectedPage = Pages.FirstOrDefault(x => x.Id == id);
        StatusText = value ? "页面已置顶" : "已取消置顶";
    }

    public async Task NavigateToPageAsync(string pageId)
    {
        if (_pageRepository is null) return;
        var context = await _pageRepository.GetContextAsync(pageId);
        if (context is null || context.Value.Page.IsDeleted) return;
        var notebook = Notebooks.FirstOrDefault(x => x.Id == context.Value.NotebookId);
        if (notebook is null) { await LoadNotebooksAsync(); notebook = Notebooks.FirstOrDefault(x => x.Id == context.Value.NotebookId); }
        if (notebook is null) return;
        SelectedNotebook = notebook; await LoadSectionsAsync(notebook);
        var section = Sections.FirstOrDefault(x => x.Id == context.Value.SectionId); if (section is null) return;
        SelectedSection = section; await LoadPagesAsync(section);
        SelectedPage = Pages.FirstOrDefault(x => x.Id == pageId);
    }

    private async Task CreateVaultAsync()
    {
        var dialog = new OpenFolderDialog { Title = "选择一个空目录或新目录作为 LocalNote 本地仓库" };
        if (dialog.ShowDialog() != true) return;
        var nameDialog = new TextInputDialog("创建本地仓库", "仓库名称：", "我的笔记") { Owner = Application.Current.MainWindow };
        if (nameDialog.ShowDialog() != true) return;
        await RunUiSafeAsync(async () =>
        {
            await CloseCurrentVaultAsync();
            var (vault, store) = await _vaultService.CreateAsync(dialog.FolderName, nameDialog.Value);
            _writeLock = VaultWriteLock.Acquire(vault.RootPath);
            await BindVaultAsync(vault, store); await EnsureStarterContentAsync(); await LoadNotebooksAsync();
            await _settings.SaveLastVaultAsync(vault.RootPath); StatusText = "本地仓库已创建";
        });
    }

    private async Task OpenVaultAsync()
    {
        var dialog = new OpenFolderDialog { Title = "打开 LocalNote 本地仓库" };
        if (dialog.ShowDialog() != true) return;
        await RunUiSafeAsync(async () => await LoadVaultAsync(dialog.FolderName));
    }

    private async Task LoadVaultAsync(string path)
    {
        await CloseCurrentVaultAsync();
        var newLock = VaultWriteLock.Acquire(path);
        try
        {
            var (vault, store) = await _vaultService.OpenAsync(path);
            _writeLock = newLock;
            await BindVaultAsync(vault, store);
            var nav = await _settings.GetNavigationStateAsync(vault.RootPath);
            _restoreNotebookId = nav.NotebookId; _restoreSectionId = nav.SectionId; _restorePageId = nav.PageId;
            if (_restoreNotebookId is not null && _restoreSectionId is not null) _lastSectionByNotebook[_restoreNotebookId] = _restoreSectionId;
            if (_restoreSectionId is not null && _restorePageId is not null) _lastPageBySection[_restoreSectionId] = _restorePageId;
            await LoadNotebooksAsync(); await _settings.SaveLastVaultAsync(vault.RootPath);
            if (!StatusText.Contains("恢复")) StatusText = "本地仓库已打开";
        }
        catch { newLock.Dispose(); if (ReferenceEquals(_writeLock, newLock)) _writeLock = null; throw; }
    }

    private async Task CloseCurrentVaultAsync()
    {
        if (_sessionState is not null) await _sessionState.EndAsync();
        _sessionState = null; _writeLock?.Dispose(); _writeLock = null;
    }

    private async Task BindVaultAsync(VaultInfo vault, SqliteDataStore store)
    {
        _vault = vault; ActiveStore = store;
        _notebookRepository = new NotebookRepository(store); _sectionRepository = new SectionRepository(store); _pageRepository = new PageRepository(store);
        ContentRepository = new ContentObjectRepository(store); InkRepository = new InkRepository(store); SearchRepository = new SearchRepository(store);
        TrashRepository = new TrashRepository(store); MediaStore = new MediaStoreService(vault.RootPath);
        BackupService = new BackupService(vault.RootPath, store); IntegrityService = new VaultIntegrityService(vault.RootPath, store);
        _sessionState = new SessionStateService(store);
        var recovered = await _sessionState.BeginAsync();
        IsVaultOpen = true; Raise(nameof(VaultSummary)); RefreshCanExecute();
        if (recovered) StatusText = "检测到上次异常退出，数据库已恢复到最后成功提交状态";
    }

    private async Task EnsureStarterContentAsync()
    {
        if (_vault is null || _notebookRepository is null || _sectionRepository is null || _pageRepository is null) return;
        var notebook = await _notebookRepository.CreateAsync(_vault.Id, "我的笔记本");
        var section = await _sectionRepository.CreateAsync(notebook.Id, "快速笔记");
        var page = await _pageRepository.CreateAsync(section.Id, "欢迎使用 LocalNote");
        if (ContentRepository is null) return;
        await ContentRepository.CreateAsync(page.Id, ContentObjectType.Text, 120, 100, 540, 190,
            "欢迎使用 LocalNote\r\n\r\n双击空白画布即可新建富文本块。所有内容只保存在本机仓库中。", "欢迎使用 LocalNote 双击空白画布 新建富文本块 本机仓库");
        await ContentRepository.CreateAsync(page.Id, ContentObjectType.Text, 720, 180, 430, 180,
            "高频操作\r\n• Ctrl + 滚轮缩放\r\n• 按住 Space + 左键拖动画布\r\n• 拖动内容块边框即可移动\r\n• Ctrl+1 将文本段落切换为待办\r\n• 右键内容块可标记重要", "高频操作 缩放 平移 画布 待办 重要 标签");
    }

    private async Task LoadNotebooksAsync()
    {
        Notebooks.Clear(); Sections.Clear(); Pages.Clear();
        _selectedNotebook = null; Raise(nameof(SelectedNotebook)); _selectedSection = null; Raise(nameof(SelectedSection)); _selectedPage = null; Raise(nameof(SelectedPage));
        if (_vault is null || _notebookRepository is null) return;
        foreach (var item in await _notebookRepository.GetActiveAsync(_vault.Id)) Notebooks.Add(item);
        var desired = _restoreNotebookId;
        _restoreNotebookId = null;
        SelectedNotebook = Notebooks.FirstOrDefault(x => x.Id == desired) ?? Notebooks.FirstOrDefault();
    }

    private async Task LoadSectionsAsync(Notebook? notebook)
    {
        Sections.Clear(); Pages.Clear(); _selectedSection = null; Raise(nameof(SelectedSection)); _selectedPage = null; Raise(nameof(SelectedPage)); ActivePageChanged?.Invoke(this, EventArgs.Empty);
        if (notebook is null || _sectionRepository is null) return;
        foreach (var item in await _sectionRepository.GetActiveAsync(notebook.Id)) Sections.Add(item);
        var desired = _restoreSectionId ?? (_lastSectionByNotebook.TryGetValue(notebook.Id, out var remembered) ? remembered : null);
        _restoreSectionId = null;
        SelectedSection = Sections.FirstOrDefault(x => x.Id == desired) ?? Sections.FirstOrDefault();
    }

    private async Task LoadPagesAsync(Section? section)
    {
        Pages.Clear(); _selectedPage = null; Raise(nameof(SelectedPage)); ActivePageChanged?.Invoke(this, EventArgs.Empty);
        if (section is null || _pageRepository is null) return;
        foreach (var item in await _pageRepository.GetActiveAsync(section.Id)) Pages.Add(item);
        var desired = _restorePageId ?? (_lastPageBySection.TryGetValue(section.Id, out var remembered) ? remembered : null);
        _restorePageId = null;
        SelectedPage = Pages.FirstOrDefault(x => x.Id == desired) ?? Pages.FirstOrDefault();
    }

    private async Task AddNotebookAsync()
    {
        if (_vault is null || _notebookRepository is null || _sectionRepository is null || _pageRepository is null) return;
        var dialog = NewNameDialog("新建笔记本", "笔记本名称：", $"新建笔记本 {Notebooks.Count + 1}"); if (dialog.ShowDialog() != true) return;
        var item = await _notebookRepository.CreateAsync(_vault.Id, dialog.Value);
        var section = await _sectionRepository.CreateAsync(item.Id, "快速笔记");
        var page = await _pageRepository.CreateAsync(section.Id, "无标题页");
        _lastSectionByNotebook[item.Id] = section.Id; _lastPageBySection[section.Id] = page.Id;
        _restoreSectionId = section.Id; _restorePageId = page.Id; _pageToFocusId = page.Id;
        Notebooks.Add(item); SelectedNotebook = item;
        StatusText = "笔记本已创建 · 已准备快速笔记页面";
    }
    private async Task RenameNotebookAsync()
    {
        if (SelectedNotebook is null || _notebookRepository is null) return;
        var dialog = NewNameDialog("重命名笔记本", "新名称：", SelectedNotebook.Name); if (dialog.ShowDialog() != true) return;
        await _notebookRepository.RenameAsync(SelectedNotebook.Id, dialog.Value); await LoadNotebooksAsync(); StatusText = "笔记本已重命名";
    }
    private async Task DeleteNotebookAsync()
    {
        if (SelectedNotebook is null || _notebookRepository is null || !Confirm($"将笔记本“{SelectedNotebook.Name}”及其内容移入回收站？")) return;
        await _notebookRepository.SoftDeleteAsync(SelectedNotebook.Id); await LoadNotebooksAsync(); StatusText = "笔记本已移入回收站";
    }
    private async Task AddSectionAsync()
    {
        if (SelectedNotebook is null || _sectionRepository is null || _pageRepository is null) return;
        var dialog = NewNameDialog("新建分区", "分区名称：", $"新建分区 {Sections.Count + 1}"); if (dialog.ShowDialog() != true) return;
        var item = await _sectionRepository.CreateAsync(SelectedNotebook.Id, dialog.Value);
        var page = await _pageRepository.CreateAsync(item.Id, "无标题页");
        _lastPageBySection[item.Id] = page.Id; _restorePageId = page.Id; _pageToFocusId = page.Id;
        Sections.Add(item); SelectedSection = item; StatusText = "分区已创建 · 已准备空白页";
    }
    private async Task RenameSectionAsync()
    {
        if (SelectedSection is null || _sectionRepository is null) return;
        var dialog = NewNameDialog("重命名分区", "新名称：", SelectedSection.Name); if (dialog.ShowDialog() != true) return;
        await _sectionRepository.RenameAsync(SelectedSection.Id, dialog.Value); await LoadSectionsAsync(SelectedNotebook); StatusText = "分区已重命名";
    }
    private async Task DeleteSectionAsync()
    {
        if (SelectedSection is null || _sectionRepository is null || !Confirm($"将分区“{SelectedSection.Name}”及其页面移入回收站？")) return;
        await _sectionRepository.SoftDeleteAsync(SelectedSection.Id); await LoadSectionsAsync(SelectedNotebook); StatusText = "分区已移入回收站";
    }
    private async Task AddPageAsync()
    {
        if (SelectedSection is null || _pageRepository is null) return;
        var item = await _pageRepository.CreateAsync(SelectedSection.Id, "无标题页");
        _pageToFocusId = item.Id;
        Pages.Add(item); SelectedPage = item; StatusText = "页面已创建 · 输入标题后即可开始记录";
    }
    private async Task RenamePageAsync()
    {
        if (SelectedPage is null || _pageRepository is null) return;
        var dialog = NewNameDialog("重命名页面", "页面标题：", SelectedPage.Title); if (dialog.ShowDialog() != true) return;
        await RenameSelectedPageInlineAsync(dialog.Value);
    }
    private async Task DeletePageAsync()
    {
        if (SelectedPage is null || _pageRepository is null || !Confirm($"将页面“{SelectedPage.Title}”移入回收站？")) return;
        await _pageRepository.SoftDeleteAsync(SelectedPage.Id); await LoadPagesAsync(SelectedSection); StatusText = "页面已移入回收站";
    }

    private async Task PersistNavigationStateAsync()
    {
        try
        {
            if (_vault is null) return;
            await _settings.SaveNavigationStateAsync(_vault.RootPath, SelectedNotebook?.Id, SelectedSection?.Id, SelectedPage?.Id);
        }
        catch { }
    }

    private TextInputDialog NewNameDialog(string title, string prompt, string initial) => new(title, prompt, initial) { Owner = Application.Current.MainWindow };
    private static bool Confirm(string message) => MessageBox.Show(Application.Current.MainWindow, message, "LocalNote", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    private async Task RunUiSafeAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { StatusText = "操作失败"; MessageBox.Show(Application.Current.MainWindow, ex.Message, "LocalNote", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void RefreshCanExecute()
    {
        AddNotebookCommand.RaiseCanExecuteChanged(); RenameNotebookCommand.RaiseCanExecuteChanged(); DeleteNotebookCommand.RaiseCanExecuteChanged();
        AddSectionCommand.RaiseCanExecuteChanged(); RenameSectionCommand.RaiseCanExecuteChanged(); DeleteSectionCommand.RaiseCanExecuteChanged();
        AddPageCommand.RaiseCanExecuteChanged(); RenamePageCommand.RaiseCanExecuteChanged(); DeletePageCommand.RaiseCanExecuteChanged();
    }
}
