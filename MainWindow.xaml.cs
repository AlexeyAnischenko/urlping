using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using LiveCharts;
using LiveCharts.Wpf;
using MathNet.Numerics.Statistics;

namespace urlping9
{
    public partial class MainWindow : Window
    {
        private ObservableCollection<UrlTestItem> _urls = new ObservableCollection<UrlTestItem>();
        private CancellationTokenSource? _cancellationTokenSource;
        private DispatcherTimer _refreshTimer;
        private int _maxDataPoints = 100000;
        private string _currentMetric = "LatestLatency";
        private DateTime _testStartTime; // Track when the test started

        // predefined set of easily distinguished colour brushes for graphs
        private static readonly List<SolidColorBrush> Brushes = new List<SolidColorBrush>
        {
            new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E6194B")), // Strong Red
            new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3CB44B")), // Bright Green
            new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0082C8")), // Vivid Blue
            new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F58231")), // Deep Orange
            new SolidColorBrush((Color)ColorConverter.ConvertFromString("#911EB4")), // Strong Purple
            new SolidColorBrush((Color)ColorConverter.ConvertFromString("#46F0F0")), // Bright Cyan
            new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F032E6")), // Vibrant Magenta
            new SolidColorBrush((Color)ColorConverter.ConvertFromString("#008080")), // Teal
            new SolidColorBrush((Color)ColorConverter.ConvertFromString("#AA6E28")), // Bold Brown
            new SolidColorBrush((Color)ColorConverter.ConvertFromString("#000080")), // Navy Blue
        };
        
        public MainWindow()
        {
            InitializeComponent();
            
            //binding the urls in urls list box with benchmark results table urls column
            UrlListBox.ItemsSource = _urls;
            StatsDataGrid.ItemsSource = CollectionViewSource.GetDefaultView(_urls); //wrapping it with WPF collection defaultView enables column sorting when clicking on titles

            //binding the urls in urls list box with url names selector for graphs
            LatencyChart.Series = new SeriesCollection();
            InitializeLegend(); // init the legent checkboxes in charts
            
            ThemeSelector.SelectedIndex = 0;
            ApplyTheme("Themes/LightTheme.xaml");

            StartTestButton.IsEnabled = false; //not enabled until at least one URL added
            StopTestButton.IsEnabled = false; //not enabled until test started

            // Initialize the refresh timer
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000) // Default 1 second
            };
            _refreshTimer.Tick += RefreshUI; //calling refreshui for graphs
        }

        private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string? selected = ((ComboBoxItem)ThemeSelector.SelectedItem)?.Content.ToString();
            if (string.IsNullOrEmpty(selected)) return;

            string themePath = selected switch
            {
                "Dark" => "Themes/DarkTheme.xaml",
                "Light" => "Themes/LightTheme.xaml",
                "System" => IsSystemThemeLight() ? "Themes/LightTheme.xaml" : "Themes/DarkTheme.xaml",
                _ => "Themes/LightTheme.xaml"
            };

            ApplyTheme(themePath);
        }

        private void ApplyTheme(string themePath)
        {
            var newDict = new ResourceDictionary() { Source = new Uri(themePath, UriKind.Relative) };
            Application.Current.Resources.MergedDictionaries.Clear();
            Application.Current.Resources.MergedDictionaries.Add(newDict);
        }

        private bool IsSystemThemeLight()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key != null)
                {
                    var value = key.GetValue("AppsUseLightTheme");
                    return (value is int i && i == 0) ? false : true;
                }
            }
            catch { } // just suppressing errors

            return true; // fallback to light theme
        }

        private void OnMetricSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ChartAxisY == null) { return; } //this prevents crashes on the 1st call
            if (MetricSelector.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag is string metricTag)
            {
                _currentMetric = metricTag;

                // Update Y axis title based on the metric selected
                switch (_currentMetric)
                {
                    case "RequestsPerSecond":
                        ChartAxisY.Title = "Requests/sec";
                        break;
                    case "ErrorsPerSecond":
                        ChartAxisY.Title = "Errors/sec";
                        break;
                    default: //all other metrics are about latencies
                        ChartAxisY.Title = "Latency (ms)";
                        break;
                }

                // Clear existing data points in chart
                foreach (var series in LatencyChart.Series)
                {
                    ((ChartValues<double>)series.Values).Clear();
                }

                // Regenerate chart data based on historic data
                RegenerateChartData();
            }
        }

        // Generate chart data based on historical samples
        private void RegenerateChartData()
        {
            // Only proceed if we're in a test
            if (_cancellationTokenSource == null || _cancellationTokenSource.IsCancellationRequested)
                return;

            foreach (var item in _urls)
            {
                if (item.ChartSeriesIndex >= 0 && item.ChartSeriesIndex < LatencyChart.Series.Count)
                {
                    var chartValues = (ChartValues<double>)LatencyChart.Series[item.ChartSeriesIndex].Values;
                    chartValues.Clear();

                    // Get metric values from history
                    var metricValues = item.CalculateMetricHistoricalValues(_currentMetric, _maxDataPoints);
                    foreach (var value in metricValues)
                    {
                        chartValues.Add(value);
                    }
                }
            }
        }

       private void OnAddUrlButtonClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var newItem = new UrlTestItem { Url = "https://example.com", RequestRate = 1 };
                _urls.Add(newItem);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error adding URL: {ex.Message}";
            }

            StartTestButton.IsEnabled = true; //cat start test with non-empty URLs list
        }

        private void OnRemoveUrlButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is UrlTestItem item)
            {
                int index = _urls.IndexOf(item);
                if (index >= 0)
                {
                    _urls.Remove(item);

                    // Remove the corresponding series from the chart
                    if (index < LatencyChart.Series.Count)
                    {
                        LatencyChart.Series.RemoveAt(index);
                        _legendItems.RemoveAt(index);
                    }
                    
                    if (_urls.Count < 1)
                    {
                        StartTestButton.IsEnabled = false; //empty URLs list, nothing to start
                    }
                }
            }
        }
public class LegendItem : INotifyPropertyChanged
{
    private bool _visible = true;
    public string Title { get; set; }
    public Brush SeriesColor { get; set; }
    public LineSeries Series { get; set; }
    
    public bool Visible
    {
        get => _visible;
        set
        {
            _visible = value;
            OnPropertyChanged();
            Series.Visibility = value ? Visibility.Visible : Visibility.Hidden;
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

private ObservableCollection<LegendItem> _legendItems = new ObservableCollection<LegendItem>();

private void InitializeLegend()
{
    LegendItemsControl.ItemsSource = _legendItems;
}

private void OnSeriesVisibilityChanged(object sender, RoutedEventArgs e)
{
    // The binding will automatically update the series visibility
}

private void AddChartSeries(UrlTestItem item)
{
    try
    {
        string urlDomain;
        try
        {
            urlDomain = new Uri(item.Url).Host;
        }
        catch
        {
            urlDomain = item.Url;
        }

        var seriesColor = GetNextBrush(LatencyChart.Series.Count);
        var series = new LineSeries
        {
            Title = urlDomain,
            Values = new ChartValues<double>(),
            PointGeometry = null,
            LineSmoothness = 0,
            Stroke = seriesColor,
            Fill = new SolidColorBrush(Colors.Transparent),
            Visibility = Visibility.Visible
        };

        LatencyChart.Series.Add(series);
        item.ChartSeriesIndex = LatencyChart.Series.Count - 1;

        // Add legend item
        _legendItems.Add(new LegendItem
        {
            Title = urlDomain,
            SeriesColor = seriesColor,
            Series = series,
            Visible = true
        });
    }
    catch (Exception ex)
    {
        StatusText.Text = $"Error adding chart series: {ex.Message}";
    }
}


        private SolidColorBrush GetNextBrush(int index)
        {
            // Use deterministic color selection based on index
            return Brushes[index % Brushes.Count];
        }

        private async void OnStartTestingButtonClick(object sender, RoutedEventArgs e)
        {
            if (_urls.Count == 0)
            {
                StatusText.Text = "Please add at least one URL to test.";
                return;
            }

            // Parse UI settings
            if (!int.TryParse(RefreshRateTextBox.Text, out int refreshRate) || refreshRate < 100)
            {
                StatusText.Text = "Invalid refresh rate. Using default 1000ms.";
                refreshRate = 1000;
            }

            // Update the refresh timer interval
            _refreshTimer.Interval = TimeSpan.FromMilliseconds(refreshRate);

            // Set the test start time
            _testStartTime = DateTime.Now;

            // Initialize the chart series for each URL
            LatencyChart.Series.Clear();
            _legendItems.Clear();
            foreach (var item in _urls)
            {
                item.ResetStats();
                item.SetTestStartTime(_testStartTime);
                AddChartSeries(item);
            }

            // Create a new cancellation token source
            _cancellationTokenSource = new CancellationTokenSource();

            // Disable UI controls
            UrlListBox.IsEnabled = false;
            StartTestButton.IsEnabled = false;
            StopTestButton.IsEnabled = true;

            // Start the UI refresh timer
            _refreshTimer.Start();

            // Update status
            StatusText.Text = "Testing started. Collecting data...";

            // Start testing for each URL
            var tasks = _urls.Select(item =>
                Task.Run(() => StartTesting(item, _cancellationTokenSource.Token))
            ).ToList();

            // Start all tasks in parallel
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (TaskCanceledException)
            {
                // Expected when cancellation is requested
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private void OnStopTestingButtonClick(object sender, RoutedEventArgs e)
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
                _refreshTimer.Stop();
                UrlListBox.IsEnabled = true;
                StartTestButton.IsEnabled = true;
                StopTestButton.IsEnabled = false;
                StatusText.Text = "Testing stopped.";
            }
        }

        private async Task StartTesting(UrlTestItem urlTestItem, CancellationToken cancellationToken)
        {
            var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var startTimestamp = DateTime.Now;
                    var response = await httpClient.GetAsync(urlTestItem.Url, cancellationToken);
                    stopwatch.Stop();

                    // Calculate latency in milliseconds
                    long latency = stopwatch.ElapsedMilliseconds;

                    // Handle successful request based on code
                    bool isSuccess = response.IsSuccessStatusCode;

                    // Update the stats in the UI thread
                    Application.Current.Dispatcher.Invoke(() => { urlTestItem.AddLatencySample(startTimestamp, latency, isSuccess); });
                }
                catch (TaskCanceledException)
                {
                    // Expected when cancellation is requested
                    break;
                }
                catch (Exception ex)
                {
                    // Update error count in the UI thread
                    Application.Current.Dispatcher.Invoke(() => { urlTestItem.AddErrorSample(); });
                }

                // Wait for the next request based on the configured rate
                if (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1.0 / urlTestItem.RequestRate), cancellationToken);
                    }
                    catch (TaskCanceledException)
                    {
                        // Expected when cancellation is requested
                        break;
                    }
                }
            }
        }

        private void RefreshUI(object? sender, EventArgs? e)
        {
            foreach (var item in _urls)
            {
                // Update chart data if we have a valid series
                if (item.ChartSeriesIndex >= 0 && item.ChartSeriesIndex < LatencyChart.Series.Count)
                {
                    var chartValues = (ChartValues<double>)LatencyChart.Series[item.ChartSeriesIndex].Values;
                    
                    // Calculate current value for selected metric
                    double currentValue = item.CalculateMetricValue(_currentMetric, DateTime.Now);
                    
                    // Add the current value to the chart
                    chartValues.Add(currentValue);

                    // Limit the number of data points
                    if (chartValues.Count > _maxDataPoints)
                    {
                        chartValues.RemoveAt(0);
                    }
                }
            }

            // Force UI updates
            StatsDataGrid.Items.Refresh();
        }
    }

    public class UrlTestItem : INotifyPropertyChanged
    {
        private string _url = "";
        private bool _visible = true;
        private int _requestRate = 1; //target requested data rate
        private double _tps = 0; //real average tps achieved, totalCount/time
        
        private long _totalCount = 0;
        private long _errorCount = 0;
        private long _minLatency = long.MaxValue;
        private long _maxLatency = 0;
        private double _avgLatency = 0;
        private double _p50Latency = 0;
        private double _p90Latency = 0;
        private double _p99Latency = 0;
        private DateTime _testStartTime;

        public string Url
        {
            get => _url;
            set => SetField(ref _url, value);
        }

        public bool Visible
        {
            get => _visible;
            set => SetField(ref _visible, value);
        }

        public int RequestRate
        {
            get => _requestRate;
            set => SetField(ref _requestRate, value);
        }

        public double TPS
        {
            get => _tps;
            set => SetField(ref _tps, value);
        }

        public long TotalCount
        {
            get => _totalCount;
            private set => SetField(ref _totalCount, value);
        }

        public long ErrorCount
        {
            get => _errorCount;
            set => SetField(ref _errorCount, value);
        }

        public long MinLatency
        {
            get => _minLatency == long.MaxValue ? 0 : _minLatency;
            private set => SetField(ref _minLatency, value);
        }

        public long MaxLatency
        {
            get => _maxLatency;
            private set => SetField(ref _maxLatency, value);
        }

        public double AvgLatency
        {
            get => _avgLatency;
            private set => SetField(ref _avgLatency, value);
        }

        public double p50Latency
        {
            get => _p50Latency;
            private set => SetField(ref _p50Latency, value);
        }

        public double p90Latency
        {
            get => _p90Latency;
            private set => SetField(ref _p90Latency, value);
        }

        public double p99Latency
        {
            get => _p99Latency;
            private set => SetField(ref _p99Latency, value);
        }

        //Index for charts
        public int ChartSeriesIndex { get; set; } = -1;
        public object LatencyLock { get; } = new object();

        // Last seen latency
        public double LastLatency { get; private set; } = 0;

        // Data for time-based measurements with complete history
        public class TimestampedSample
        {
            public DateTime Timestamp { get; set; }
            public double Value { get; set; }
            public bool IsError { get; set; }
        }

        // Store complete history of all samples
        private List<TimestampedSample> _allSamples = new List<TimestampedSample>();

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        public void SetTestStartTime(DateTime startTime)
        {
            _testStartTime = startTime;
        }

        public void ResetStats()
        {
            TotalCount = 0;
            TPS = 0;
            ErrorCount = 0;
            MinLatency = long.MaxValue;
            MaxLatency = 0;
            AvgLatency = 0;
            p50Latency = 0;
            p90Latency = 0;
            p99Latency = 0;
            LastLatency = 0;

            lock (LatencyLock)
            {
                _allSamples.Clear();
            }
        }

        private double CalculatePercentile(List<double> sortedData, int percentile)
        {
            if (sortedData.Count == 0)
                return 0;
            
            return sortedData.Percentile(percentile);
        }

        public void AddLatencySample(DateTime timestamp, double latency, bool isSuccess)
        {
            lock (LatencyLock)
            {
                // Update overall stats
                TotalCount++;
                LastLatency = latency;

                // Update min/max
                if (latency < MinLatency) MinLatency = (long)latency;
                if (latency > MaxLatency) MaxLatency = (long)latency;

                // Add to all samples
                //we need to pass in the timestamp of request-start here, not just using "now" -
                //as here we have the moment after response-finished 
                _allSamples.Add(new TimestampedSample
                {
                    Timestamp = timestamp,
                    Value = latency,
                    IsError = !isSuccess
                });

                // If not successful, update the error count
                if (!isSuccess) ErrorCount++;

                // Calculate statistics using all successful samples
                var successfulSamples = _allSamples.Where(s => !s.IsError).Select(s => s.Value).ToList();

                // Update average on successful samples only
                AvgLatency = successfulSamples.Any() ? successfulSamples.Average() : 0;
                //Update TPS for successful samples only
                TPS = successfulSamples.Count / (timestamp.AddMilliseconds(latency)  - _testStartTime).TotalSeconds;

                // Update percentiles if we have enough data
                if (successfulSamples.Count >= 10)
                {
                    var sortedData = successfulSamples.OrderBy(x => x).ToList();
                    p50Latency = CalculatePercentile(sortedData, 50);
                    p90Latency = CalculatePercentile(sortedData, 90);
                    p99Latency = CalculatePercentile(sortedData, 99);
                }
            }
        }

        public void AddErrorSample()
        {
            lock (LatencyLock)
            {
                ErrorCount++;
                _allSamples.Add(new TimestampedSample
                {
                    Timestamp = DateTime.Now,
                    Value = 0,
                    IsError = true
                });
            }
        }

        // Calculate current value for a given metric
        public double CalculateMetricValue(string metricName, DateTime endTime)
        {
            lock (LatencyLock)
            {
                switch (metricName)
                {
                    case "LatestLatency":
                        return LastLatency;
                    case "RequestsPerSecond":
                        return CalculateRequestsPerSecond(endTime);
                    case "ErrorsPerSecond":
                        return CalculateErrorsPerSecond(endTime);
                    case "Min1Sec":
                        return CalculateSlidingWindowMin(1, endTime);
                    case "Max1Sec":
                        return CalculateSlidingWindowMax(1, endTime);
                    case "Avg1Sec":
                        return CalculateSlidingWindowAvg(1, endTime);
                    case "P25_1Sec":
                        return CalculateSlidingWindowPercentile(1, 25, endTime);
                    case "P50_1Sec":
                        return CalculateSlidingWindowPercentile(1, 50, endTime);
                    case "P75_1Sec":
                        return CalculateSlidingWindowPercentile(1, 75, endTime);
                    case "P90_1Sec":
                        return CalculateSlidingWindowPercentile(1, 90, endTime);
                    case "P99_1Sec":
                        return CalculateSlidingWindowPercentile(1, 99, endTime);
                    default:
                        return 0;
                }
            }
        }

        // Calculate historical values for a given metric
        public List<double> CalculateMetricHistoricalValues(string metricName, int maxPoints)
        {
            lock (LatencyLock)
            {
                // Ensure we have at least two samples
                if (_allSamples.Count < 2)
                    return new List<double>();

                // Get the time interval to show on chart
                DateTime oldestTime = _allSamples[0].Timestamp;
                DateTime newestTime = _allSamples[_allSamples.Count - 1].Timestamp;
                
                // Calculate time interval between points
                TimeSpan totalTimeSpan = newestTime - oldestTime;
                double intervalSeconds = totalTimeSpan.TotalSeconds / maxPoints;
                
                // Ensure minimum interval is 1 second
                intervalSeconds = Math.Max(intervalSeconds, 1);
                
                List<double> result = new List<double>();
                
                // Generate data points at regular intervals
                for (int i = 0; i < maxPoints; i++)
                {
                    DateTime pointTime = oldestTime.AddSeconds(i * intervalSeconds);
                    
                    // Skip future times
                    if (pointTime > newestTime)
                        break;
                        
                    double value = CalculateMetricValue(metricName, pointTime);
                    result.Add(value);
                }
                
                return result;
            }
        }

        //counts number of request sample entries within the specified interval window, can't be fractional
        public int CalculateRequestsPerSecond(DateTime now)
        {
            lock (LatencyLock)
            {
                // Count requests in the last second
                DateTime oneSecondAgo = now.AddSeconds(-1);
                return _allSamples.Count(s => (s.Timestamp >= oneSecondAgo && s.Timestamp < now));
            }
        }

        public double CalculateErrorsPerSecond(DateTime now)
        {
            lock (LatencyLock)
            {
                // Count errors in the last second
                DateTime oneSecondAgo = now.AddSeconds(-1);
                return _allSamples.Count(s => (s.Timestamp >= oneSecondAgo && s.Timestamp < now && s.IsError));
            }
        }

        public double CalculateSlidingWindowMin(int seconds, DateTime now)
        {
            lock (LatencyLock)
            {
                DateTime windowStart = now.AddSeconds(-seconds);
                var windowSamples = _allSamples
                    .Where(s => s.Timestamp >= windowStart && s.Timestamp < now && !s.IsError)
                    .Select(s => s.Value)
                    .ToList();

                return windowSamples.Any() ? windowSamples.Min() : 0;
            }
        }

        public double CalculateSlidingWindowMax(int seconds, DateTime now)
        {
            lock (LatencyLock)
            {
                DateTime windowStart = now.AddSeconds(-seconds);
                var windowSamples = _allSamples
                    .Where(s => s.Timestamp >= windowStart && s.Timestamp < now && !s.IsError)
                    .Select(s => s.Value)
                    .ToList();

                return windowSamples.Any() ? windowSamples.Max() : 0;
            }
        }

        public double CalculateSlidingWindowAvg(int seconds, DateTime now)
        {
            lock (LatencyLock)
            {
                DateTime windowStart = now.AddSeconds(-seconds);
                var windowSamples = _allSamples
                    .Where(s => s.Timestamp >= windowStart && s.Timestamp < now && !s.IsError)
                    .Select(s => s.Value)
                    .ToList();

                return windowSamples.Any() ? windowSamples.Average() : 0;
            }
        }

        public double CalculateSlidingWindowPercentile(int seconds, int percentile, DateTime now)
        {
            lock (LatencyLock)
            {
                DateTime windowStart = now.AddSeconds(-seconds);
                var windowSamples = _allSamples
                    .Where(s => s.Timestamp >= windowStart && s.Timestamp < now && !s.IsError)
                    .Select(s => s.Value)
                    .OrderBy(v => v)
                    .ToList();

                return windowSamples.Any() ? CalculatePercentile(windowSamples, percentile) : 0;
            }
        }
    }
}