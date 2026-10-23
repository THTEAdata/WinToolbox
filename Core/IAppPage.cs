namespace WinToolbox.Core;

/// <summary>页面生命周期：切换到时通知、窗口关闭时释放资源。</summary>
public interface IAppPage
{
    void OnActivated();
    void OnDeactivated();
    void OnClosed();
}
