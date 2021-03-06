using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Newtonsoft.Json;
using System.ComponentModel;
using System.Linq;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Forms;
using System.Windows.Threading;
using System.Xml.Linq;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Wpf;
using LinearAxis = OxyPlot.Wpf.LinearAxis;
using LineSeries = OxyPlot.Wpf.LineSeries;
using VerticalAlignment = System.Windows.VerticalAlignment;
using System.IO;
using Newtonsoft.Json.Linq;

namespace LSLImportCurves
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window, INotifyPropertyChanged, ISaveEEGStreamToFile
    {
        private ObservableCollection<ComboBoxItem> _cbItems;
        private ComboBoxItem _selectedcbItem;
        private const int BufferLen = 2000;
        private List<DataPoint[]> _curves;
        private List<Plot> _plots = new List<Plot>();
        private bool _run;
        private int _channelsCount;
        private liblsl.StreamInlet _inlet;
        private liblsl.StreamInfo[] _allStreams;
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private bool saveEnabled;
        private List<string> axisNames = new List<string>();
        private string currentStreamName;



        public FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog();

        public string PathToSelectedFolder;

        public string CurrentStreamName
        {
            get { return currentStreamName; }
            set { currentStreamName = value; OnPropertyChanged(); }
        }

        public ObservableCollection<StreamModel> Streams { get; set; }

        public List<string> AxisNames
        {
            get { return axisNames; }
            set { axisNames = value; OnPropertyChanged(); }
        }

        public bool SaveEnabled
        {
            get { return saveEnabled; }
            set { saveEnabled = value; OnPropertyChanged(); }
        }

        public List<DataPoint[]> Curves
        {
            get => _curves;
            set
            {
                _curves = value;
                OnPropertyChanged();
            }
        }
        public ObservableCollection<ComboBoxItem> CbItems
        {
            get => _cbItems;
            set
            {
                _cbItems = value;
                OnPropertyChanged();
            }
        }
        public ComboBoxItem SelectedcbItem
        {
            get => _selectedcbItem;
            set
            {
                _selectedcbItem = value;
                OnPropertyChanged();
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            FindStream();
            CheckIfSavingEnabled();
        }

        /// <summary>
        /// Ищет открытые потоки и отображает их в combobox
        /// </summary>
        private async Task FindStream()
        {
            _allStreams = await Task.Run(() => liblsl.resolve_streams());
            
            CbItems = new ObservableCollection<ComboBoxItem>();
            var cbItem = new ComboBoxItem { Content = "<--Select-->" };
            SelectedcbItem = cbItem;
            CbItems.Add(cbItem);

            foreach (var stream in _allStreams)
            {
                CbItems.Add(new ComboBoxItem { Content = stream.name() });
                CurrentStreamName = stream.name();
            }
        }

        /// <summary>
        /// В соответствии с данными из xml создает нужное количество кривых
        /// </summary>
        /// <returns></returns>
        private bool Prepare()
        {
            if (cbStream.SelectedIndex - 1 < 0) return false;
            _inlet = new liblsl.StreamInlet(_allStreams[cbStream.SelectedIndex - 1]);
            tbXmlInfo.Text = _inlet.info().as_xml();

            var xml = XElement.Parse(_inlet.info().as_xml());
            var channels = xml.Element("desc").Element("channels").Elements("channel").ToList();

            _channelsCount = _inlet.info().channel_count();
            _plots = new List<Plot>();
            CurvesGrid.Children.Clear();
            AxisNames = new List<string>();
            CurvesGrid.RowDefinitions.Clear();
            CurvesGrid.ColumnDefinitions.Clear();
            CurvesGrid.ColumnDefinitions.Add(new ColumnDefinition(){Width = GridLength.Auto});
            CurvesGrid.ColumnDefinitions.Add(new ColumnDefinition());
            for (var i = 0; i < _channelsCount; i++)
            {
                CurvesGrid.RowDefinitions.Add(new RowDefinition());

                var sp = new StackPanel() {Orientation = (System.Windows.Controls.Orientation)System.Windows.Forms.Orientation.Horizontal};
                var sp2 = new StackPanel() {VerticalAlignment = VerticalAlignment.Center};
                var label = new TextBlock() {Text = channels[i].Element("label").Value};
                AxisNames.Add(label.Text); // заполняем массив с именами осей
                var type = new TextBlock() {Text = channels[i].Element("type").Value};
                sp2.Children.Add(label);
                sp2.Children.Add(type);
                sp.Children.Add(sp2);
                sp.SetValue(Grid.RowProperty, i);

                var plot = new Plot(){ Margin = new Thickness(0, -2, 0, -5)};

                //убираем оси
                plot.Axes.Add(new LinearAxis()
                {
                    Position = AxisPosition.Bottom,
                    IsAxisVisible = false,
                });
                plot.Axes.Add(new LinearAxis()
                {
                    Position = AxisPosition.Left,
                    IsAxisVisible = false
                });
                plot.SetValue(Grid.RowProperty, i);
                plot.SetValue(Grid.ColumnProperty, 1);
                var ls = new LineSeries();
                var myBinding = new System.Windows.Data.Binding
                {
                    ElementName = "Root",
                    Path = new PropertyPath("Curves[" + i + "]"),
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                };
                BindingOperations.SetBinding(ls, LineSeries.ItemsSourceProperty, myBinding);
                plot.Series.Add(ls);    
                _plots.Add(plot);

                CurvesGrid.Children.Add(sp);
                CurvesGrid.Children.Add(plot);
            }

            Curves = new List<DataPoint[]>();
            for (var i = 0; i < _channelsCount; i++)
            {
                Curves.Add(new DataPoint[BufferLen]);
                for (var j = 0; j < Curves[i].Length; j++)
                    Curves[i][j] = new DataPoint(j, 0);
            }
            return true;
        }

        /// <summary>
        /// Чтение данных кривых
        /// </summary>
        private void Read()
        {
            var index = 0;
            var lslBuffLen = 4096;

            // read samples
            var buffer = new float[lslBuffLen, _channelsCount];
            var timestamps = new double[lslBuffLen];
            while (_run)
            {
                var num = _inlet.pull_chunk(buffer, timestamps, 0.05);

                for (var s = 0; s < num; s++)
                {
                    for (var c = 0; c < _channelsCount; c++)
                    {
                        Curves[c][index % BufferLen] = new DataPoint((index % BufferLen), buffer[s, c]);
                    }
                    index++;
                    if ((index % BufferLen) == 0)
                        for (var c = 0; c < _channelsCount; c++)
                        {
                            for (var i = 0; i < BufferLen; i++)
                                Curves[c][i] = new DataPoint(i, 0);
                        }
                }
            }
        }

        private void UpdateCurves()
        {
            foreach (var p in _plots)
                p.InvalidatePlot();
        }

        private async void ButtonUpdate_OnClick(object sender, RoutedEventArgs e)
        {
            await FindStream();
        }

        private async void ButtonRead_OnClick(object sender, RoutedEventArgs e)
        {
            btStart.IsEnabled = false;
            _run = true;

            _timer.Interval = new TimeSpan(0, 0, 0, 0, 50);
            _timer.IsEnabled = true;
            _timer.Tick += (o, args) =>
            {
                UpdateCurves();
            };
            _timer.Start();

            if (!Prepare()) return;
            await Task.Run(() => Read());
        }

        private void ButtonStop_OnClick(object sender, RoutedEventArgs e)
        {
            btStart.IsEnabled = true;
            _timer.Stop();
            _timer.IsEnabled = false;
            _run = false;
            if (SaveEnabled)
            {
                SaveStreamInfoToJson(AxisNames, Curves, PathToSelectedFolder, CurrentStreamName);
            }
        }

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion

        private void ButtonSelectFolder_OnClick(object sender, RoutedEventArgs e)
        {
            folderBrowserDialog.ShowDialog();
            PathToSelectedFolder = folderBrowserDialog.SelectedPath;
        }

        private void SaveBox_Checked(object sender, RoutedEventArgs e)
        {
            SaveEnabled = true;
            CheckIfSavingEnabled();
        }

        private void SaveBox_Unchecked(object sender, RoutedEventArgs e)
        {
            SaveEnabled = false;
            CheckIfSavingEnabled();
        }

        private void CheckIfSavingEnabled()
        {
            if (SaveEnabled)
            {
                this.btSelectFolder.IsEnabled = true;
            }
            else this.btSelectFolder.IsEnabled = false;
        }

        public void SaveStreamInfoToJson(List<string> axisLabels, List<DataPoint[]> points, string pathToFolder, string streamName)
        {
            string path = $"{pathToFolder}\\{streamName.Replace(':', '.')}.json";
            Streams = new ObservableCollection<StreamModel>();
            Streams.Add(new StreamModel() {StreamName = streamName, AxisLabels = axisLabels, Points = points });
            object o = Streams;
            if (path != null)
            {
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir))
                {
                    _ = Directory.CreateDirectory(dir);
                }

                using (StreamWriter file = File.CreateText(path))
                {
                    JsonSerializer serializer = new JsonSerializer();
                    serializer.Formatting = Formatting.Indented;
                    serializer.Serialize(file, o);
                }
            }
        }

        public static bool IsValidJson(string stringValue)
        {
            if (File.Exists(stringValue))
            {
                var value = File.ReadAllText(stringValue).Trim();
                if ((value.StartsWith("{") && value.EndsWith("}")) ||
                    (value.StartsWith("[") && value.EndsWith("]")))
                {
                    try
                    {
                        JToken obj = JToken.Parse(value);
                        return true;
                    }
                    catch (JsonReaderException)
                    {
                        return false;
                    }
                }
            }
            return false;
        }
    }

    public class StreamModel : INotifyPropertyChanged
    {
        private string streamName;

        public string StreamName
        {
            get { return streamName; }
            set { streamName = value; }
        }

        private List<string> axisLabels;

        public List<string> AxisLabels
        {
            get { return axisLabels; }
            set { axisLabels = value; OnPropertyChanged(); }
        }

        private List<DataPoint[]> points;

        public List<DataPoint[]> Points
        {
            get { return points; }
            set { points = value; OnPropertyChanged(); }
        }


        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }

    public interface ISaveEEGStreamToFile
    {
        /// <summary>
        /// Сохраняет графики с ЭЭГ в JSON файл
        /// </summary>
        /// <param name="axisLabels">Названия осей</param>
        /// <param name="points">Список точек графика</param>
        /// <param name="pathToDestinationFolder">Путь к папке, в которой будет сохранено</param>
        /// <param name="streamName">Название потока</param>
        void SaveStreamInfoToJson(List<string> axisLabels, List<DataPoint[]> points, string pathToDestinationFolder, string streamName);
    }
}
