using System;
using System.Linq;
using System.Windows;

namespace FFmpegNativePlayer;

public partial class ScheduleDialog : Window
{
    private readonly double _durationSeconds;

    public DateTime StartWhen { get; private set; }
    public DateTime? EndWhen { get; private set; }
    public string StartMode { get; private set; } = "EXACT";
    public string EndMode { get; private set; } = "MANUAL END";
    public bool ExactEnd { get; private set; } = true;
    public bool AutoChain { get; private set; } = true;

    public ScheduleDialog(string programName, double durationSeconds, DateTime? suggestedStart = null)
    {
        InitializeComponent();
        _durationSeconds = Math.Max(0, durationSeconds);
        ProgramNameText.Text = programName;
        DurationText.Text = $"Duration: {Clock(_durationSeconds)}";

        var hours = Enumerable.Range(1, 12).Select(v => v.ToString("00")).ToArray();
        var mins = Enumerable.Range(0, 60).Select(v => v.ToString("00")).ToArray();
        foreach (var box in new[] { StartHour, EndHour }) box.ItemsSource = hours;
        foreach (var box in new[] { StartMinute, StartSecond, EndMinute, EndSecond }) box.ItemsSource = mins;
        StartAmPm.ItemsSource = new[] { "AM", "PM" };
        EndAmPm.ItemsSource = new[] { "AM", "PM" };

        var initialStart = suggestedStart ?? DateTime.Now;
        SetStart(initialStart);
        SetEnd(initialStart.AddSeconds(_durationSeconds > 0 ? _durationSeconds : 3600));
        AutoEndCheck.IsChecked = true;
        AutoChainCheck.IsChecked = true;
    }

    private static string Clock(double s) =>
        TimeSpan.FromSeconds(Math.Max(0, s)).ToString(s >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss");

    private void SetStart(DateTime value)
    {
        StartDatePicker.SelectedDate = value.Date;
        SetClock(StartHour, StartMinute, StartSecond, StartAmPm, value);
    }

    private void SetEnd(DateTime value) => SetClock(EndHour, EndMinute, EndSecond, EndAmPm, value);

    private static void SetClock(System.Windows.Controls.ComboBox h, System.Windows.Controls.ComboBox m,
        System.Windows.Controls.ComboBox s, System.Windows.Controls.ComboBox ap, DateTime value)
    {
        int hh = value.Hour % 12;
        if (hh == 0) hh = 12;
        h.SelectedItem = hh.ToString("00");
        m.SelectedItem = value.Minute.ToString("00");
        s.SelectedItem = value.Second.ToString("00");
        ap.SelectedItem = value.Hour >= 12 ? "PM" : "AM";
    }

    private static bool TryClock(System.Windows.Controls.ComboBox h, System.Windows.Controls.ComboBox m,
        System.Windows.Controls.ComboBox s, System.Windows.Controls.ComboBox ap, out TimeSpan value)
    {
        value = default;
        if (h.SelectedItem is not string hs || m.SelectedItem is not string ms ||
            s.SelectedItem is not string ss || ap.SelectedItem is not string aps ||
            !int.TryParse(hs, out int hour12) || !int.TryParse(ms, out int minute) ||
            !int.TryParse(ss, out int second))
            return false;

        int hour24 = hour12 % 12;
        if (string.Equals(aps, "PM", StringComparison.OrdinalIgnoreCase)) hour24 += 12;
        value = new TimeSpan(hour24, minute, second);
        return true;
    }

    private void ResetNow_Click(object sender, RoutedEventArgs e)
    {
        SetStart(DateTime.Now);
        if (AutoEndCheck.IsChecked == true && _durationSeconds > 0)
            SetEnd(DateTime.Now.AddSeconds(_durationSeconds));
    }

    private void AutoEndCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoEndCheck.IsChecked == true)
        {
            ContinueCheck.IsChecked = false;
            ExactEndCheck.IsChecked = true;
            if (TryGetStart(out var start) && _durationSeconds > 0)
                SetEnd(start.AddSeconds(_durationSeconds));
        }
    }

    private void ContinueCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (ContinueCheck.IsChecked == true)
        {
            AutoEndCheck.IsChecked = false;
            ExactEndCheck.IsChecked = false;
        }
    }

    private bool TryGetStart(out DateTime start)
    {
        start = default;
        if (StartDatePicker.SelectedDate is not DateTime date ||
            !TryClock(StartHour, StartMinute, StartSecond, StartAmPm, out var time))
            return false;
        start = date.Date + time;
        return true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetStart(out var start))
        {
            System.Windows.MessageBox.Show("Select Start Date and Start Time.", "SMART PLAYOUT",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StartWhen = start;
        StartMode = TimeModeRadio.IsChecked == true ? "TIME" : "EXACT";
        AutoChain = AutoChainCheck.IsChecked == true;

        if (ContinueCheck.IsChecked == true)
        {
            EndWhen = null;
            EndMode = "CONTINUE";
            ExactEnd = false;
        }
        else if (AutoEndCheck.IsChecked == true)
        {
            if (_durationSeconds <= 0)
            {
                System.Windows.MessageBox.Show("Program duration is not available for AUTO END.", "SMART PLAYOUT",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            EndWhen = start.AddSeconds(_durationSeconds);
            EndMode = "AUTO DURATION";
            ExactEnd = ExactEndCheck.IsChecked == true;
        }
        else
        {
            if (!TryClock(EndHour, EndMinute, EndSecond, EndAmPm, out var endTime))
            {
                System.Windows.MessageBox.Show("Select End Time.", "SMART PLAYOUT",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var end = start.Date + endTime;
            if (end <= start) end = end.AddDays(1);
            EndWhen = end;
            EndMode = "MANUAL END";
            ExactEnd = ExactEndCheck.IsChecked == true;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
