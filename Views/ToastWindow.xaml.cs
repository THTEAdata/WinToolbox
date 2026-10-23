using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WinToolbox.Core;

namespace WinToolbox.Views;

/// <summary>右下角轻量提示（低电量提醒）。</summary>
public partial class ToastWindow : Window
{
    private static ToastWindow? _current;
    private readonly DispatcherTimer _closeTimer;

    private ToastWindow(string body)
    {
        InitializeComponent();
        BodyText.Text = body;
        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _closeTimer.Tick += (_, _) => Close();
        MouseLeftButtonUp += (_, _) =>
        {
            try
            {
                if (Application.Current.MainWindow is { } main)
                {
                    main.WindowState = WindowState.Normal;
                    main.Activate();
                    (main as MainWindow)?.NavigateTo("battery");
                }
            }
            catch { }
            Close();
        };
    }

    public static void Show(string body)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            _current?.Close();

            var toast = new ToastWindow(body);
            _current = toast;
            toast.Show();
            toast.PositionBottomRight();
            toast.BeginFade();
            toast._closeTimer.Start();
            toast.Closed += (_, _) => { if (ReferenceEquals(_current, toast)) _current = null; };
        });
    }

    private void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 20;
        Top = area.Bottom - ActualHeight - 20;
    }

    private void BeginFade()
    {
        Opacity = 0;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220));
        BeginAnimation(OpacityProperty, fade);

        var slide = new DoubleAnimation(Top + 16, Top, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(TopProperty, slide);
    }
}
