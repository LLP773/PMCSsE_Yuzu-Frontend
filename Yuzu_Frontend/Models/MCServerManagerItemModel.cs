using PMCSsE_Communicator;
using ReactiveUI;

namespace Yuzu_Frontend.Models;

public class MCServerManagerItemModel : ReactiveObject
{
    private string _managerId = "";
    private string _mcServerName = "";
    private string _mcServerType = "Vanilla";
    private string _mcServerDirectory = "";
    private string _javaPath = "";
    private string _startUpArguments = "";
    private bool _isMCServerRunning;
    private bool _isLoaded;

    public string ManagerId
    {
        get => _managerId;
        set => this.RaiseAndSetIfChanged(ref _managerId, value);
    }

    public string MCServerName
    {
        get => _mcServerName;
        set => this.RaiseAndSetIfChanged(ref _mcServerName, value);
    }

    public string MCServerType
    {
        get => _mcServerType;
        set => this.RaiseAndSetIfChanged(ref _mcServerType, value);
    }

    public string MCServerDirectory
    {
        get => _mcServerDirectory;
        set => this.RaiseAndSetIfChanged(ref _mcServerDirectory, value);
    }

    public string JavaPath
    {
        get => _javaPath;
        set => this.RaiseAndSetIfChanged(ref _javaPath, value);
    }

    public string StartUpArguments
    {
        get => _startUpArguments;
        set => this.RaiseAndSetIfChanged(ref _startUpArguments, value);
    }

    public bool IsMCServerRunning
    {
        get => _isMCServerRunning;
        set => this.RaiseAndSetIfChanged(ref _isMCServerRunning, value);
    }

    public bool IsLoaded
    {
        get => _isLoaded;
        set => this.RaiseAndSetIfChanged(ref _isLoaded, value);
    }

    public string MCServerStatus => IsMCServerRunning ? "运行中" : IsLoaded ? "已加载" : "未加载";

    public MCServerManagerItemModel() { }

    public MCServerManagerItemModel(MCServerManagerConfig config)
    {
        ManagerId = config.ManagerID;
        MCServerName = config.MCServerName;
        MCServerType = config.MCServerType;
        MCServerDirectory = config.MCServerDirectory;
        JavaPath = config.JavaPath;
        StartUpArguments = config.StartUpArguments;
    }
}