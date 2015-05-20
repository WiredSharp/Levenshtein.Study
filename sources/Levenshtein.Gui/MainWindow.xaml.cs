using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json;

namespace Levenshtein.Gui
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const string SYSTEMS_FILEPATH = "systems.json";
        private Random _ran;
        private IEnumerable<string> _data;
        private bool _initialized;

        public MainWindow()
        {
            InitializeComponent();
            _ran = new Random((int)DateTime.Now.Ticks);
            Task.Factory.StartNew(Initialize, new CancellationToken(), TaskCreationOptions.None, TaskScheduler.Default)
                .ContinueWith(t => { lvData.ItemsSource = _data; _initialized = true; }, new CancellationToken(), TaskContinuationOptions.NotOnFaulted, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void Initialize()
        {
            _data = GetSystems().Select(s => s.Name).ToArray(); //BuildData(100, "monitoring");
            _initialized = true;
        }

        private EddbSystem[] GetSystems()
        {
            EddbSystem[] data = null;
            try
            {
                if (!File.Exists(SYSTEMS_FILEPATH))
                {
                    using (var client = new WebClient())
                    {
                        client.DownloadFile(new Uri("http://eddb.io/archive/v3/systems.json"), SYSTEMS_FILEPATH);
                    }
                }
                using (var reader = new StreamReader(SYSTEMS_FILEPATH))
                using (var jreader = new JsonTextReader(reader))
                {
                    try
                    {
                        JsonSerializer serializer = new JsonSerializer();
                        data = serializer.Deserialize<EddbSystem[]>(jreader);
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceError("serialization failure: " + ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError("system data download failed: " + ex);
            }
            return data ?? new EddbSystem[0];
        }

        private IEnumerable<string> BuildData(int count, string seed)
        {
            var data = new List<string>() { seed };
            for (int i = 0; i < count; i++)
            {
                seed = Migrate(seed);
                data.Add(seed);
            }
            return data;
        }

        private string Migrate(string seed)
        {
            int pos = _ran.Next(seed.Length - 1);
            int action = _ran.Next(32, 126);
            seed = seed.Remove(pos, 1);
            if (action != 126)
            {
                seed = seed.Insert(pos, ((char)action).ToString());
            }
            return seed;
        }

        /// <summary>
        /// Compute the accuracy matching.
        /// </summary>
        public static double Compute(string s, string t)
        {
            int n = s.Length;
            int m = t.Length;
            // Step 1
            if (n == 0)
            {
                return 0;
            }

            if (m == 0)
            {
                return 0;
            }

            s = s.ToLowerInvariant();
            t = t.ToLowerInvariant();

            int[,] d = new int[n + 1, m + 1];

            // Step 2
            for (int i = 0; i <= n; ++i)
            {
                d[i, 0] = i;
            }

            for (int j = 0; j <= m; ++j)
            {
                d[0, j] = j;
            }

            // Step 3
            for (int i = 1; i <= n; ++i)
            {
                //Step 4
                for (int j = 1; j <= m; ++j)
                {
                    // Step 5
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;

                    // Step 6
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }
            // Step 7
            var max = Math.Max(n, m);
            return (100 * (max - d[n, m])) / (double)max;
        }

        private void TbInputTextChangedEventHandler(object sender, TextChangedEventArgs e)
        {
            if (_initialized && tbInput.Text.Length > 3)
            {
                RunCompute(tbInput.Text, _data, new LevenshteinWrapper(Compute));
                //RunCompute(tbInput.Text, _data, new LevenshteinWrapper(LevenshteinImpl.iLD));
                //RunCompute(tbInput.Text, _data, new LevenshteinWrapper(LevenshteinImpl.LD));
            }
        }

        private void RunCompute(string text, IEnumerable<string> data, ILevenshteinComputor compute)
        {
            try
            {
                Task.Factory.StartNew(
                    () =>
                        compute.Process(text, data)
                    )
                    .ContinueWith(
                        task =>
                        {
                            if (!task.IsFaulted)
                            {
                                lvResult.ItemsSource = task.Result;
                                lvStat.Items.Add(compute.Description + ": " + compute.Elapsed.ToString());
                            }
                            else
                            {
                                if (task.Exception != null)
                                    lvResult.ItemsSource = new[] { task.Exception.ToString() };
                            }
                        }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            catch (Exception ex)
            {
                lvResult.ItemsSource = new string[] { ex.Message };
            }
        }
    }

    internal class EddbSystem
    {
        [JsonProperty(PropertyName = "name")]
        public string Name { get; set; }
    }

    internal interface ILevenshteinComputor
    {
        string Description { get; }

        TimeSpan Elapsed { get; }

        string[] Process(string s, IEnumerable<string> data);
    }

    internal class LevenshteinWrapper : ILevenshteinComputor
    {
        private const int ITEMS_KEPT_COUNT = 10;

        private readonly Func<string, string, double> _compute;
        public string Description { get; set; }
        public TimeSpan Elapsed { get; private set; }

        public LevenshteinWrapper(Func<string,string,double> compute)
        {
            _compute = compute;
        }

        public string[] Process(string s, IEnumerable<string> data)
        {
            var timer = new Stopwatch();
            timer.Start();
            var hints = data.Select(d => new KeyValuePair<double, string>(_compute(s,d), d))
                .OrderByDescending(kvp => kvp.Key)
                .Take(ITEMS_KEPT_COUNT)
                .Select(kvp => kvp.Value + " (" + kvp.Key + "%)")
                .ToArray();
            timer.Stop();
            Elapsed = timer.Elapsed;
            return hints;
        }
    }

    public class LevenshteinImpl
    {
        ///*****************************
        /// Compute Levenshtein distance 
        /// Memory efficient version
        ///*****************************
        public static double iLD(String sRow, String sCol)
        {
            int RowLen = sRow.Length;  // length of sRow
            int ColLen = sCol.Length;  // length of sCol
            int RowIdx;                // iterates through sRow
            int ColIdx;                // iterates through sCol
            char Row_i;                // ith character of sRow
            char Col_j;                // jth character of sCol
            int cost;                   // cost

            /// Test string length
            if (Math.Max(sRow.Length, sCol.Length) > Math.Pow(2, 31))
                throw (new Exception("\nMaximum string length in Levenshtein.iLD is " + Math.Pow(2, 31) + ".\nYours is " + Math.Max(sRow.Length, sCol.Length) + "."));

            // Step 1

            if (RowLen == 0)
            {
                return ColLen;
            }

            if (ColLen == 0)
            {
                return RowLen;
            }

            /// Create the two vectors
            int[] v0 = new int[RowLen + 1];
            int[] v1 = new int[RowLen + 1];
            int[] vTmp;



            /// Step 2
            /// Initialize the first vector
            for (RowIdx = 1; RowIdx <= RowLen; RowIdx++)
            {
                v0[RowIdx] = RowIdx;
            }

            // Step 3

            /// Fore each column
            for (ColIdx = 1; ColIdx <= ColLen; ColIdx++)
            {
                /// Set the 0'th element to the column number
                v1[0] = ColIdx;

                Col_j = sCol[ColIdx - 1];


                // Step 4

                /// Fore each row
                for (RowIdx = 1; RowIdx <= RowLen; RowIdx++)
                {
                    Row_i = sRow[RowIdx - 1];


                    // Step 5

                    if (Row_i == Col_j)
                    {
                        cost = 0;
                    }
                    else
                    {
                        cost = 1;
                    }

                    // Step 6

                    /// Find minimum
                    int m_min = v0[RowIdx] + 1;
                    int b = v1[RowIdx - 1] + 1;
                    int c = v0[RowIdx - 1] + cost;

                    if (b < m_min)
                    {
                        m_min = b;
                    }
                    if (c < m_min)
                    {
                        m_min = c;
                    }

                    v1[RowIdx] = m_min;
                }

                /// Swap the vectors
                vTmp = v0;
                v0 = v1;
                v1 = vTmp;

            }


            // Step 7

            /// Value between 0 - 100
            /// 100==perfect match 0==totaly different
            /// 
            /// The vectors where swaped one last time at the end of the last loop,
            /// that is why the result is now in v0 rather than in v1
            //System.Console.WriteLine("iDist=" + v0[RowLen]);
            int max = Math.Max(RowLen, ColLen);
            return (100.0 * (max-v0[RowLen])) / max;
        }

        ///*****************************
        /// Compute the min
        ///*****************************
        private static int Minimum(int a, int b, int c)
        {
            int mi = a;
            if (b < mi)
            {
                mi = b;
            }
            if (c < mi)
            {
                mi = c;
            }
            return mi;
        }

        ///*****************************
        /// Compute Levenshtein distance         
        ///*****************************
        public static double LD(String sNew, String sOld)
        {
            int[,] matrix;              // matrix
            int sNewLen = sNew.Length;  // length of sNew
            int sOldLen = sOld.Length;  // length of sOld
            int sNewIdx; // iterates through sNew
            int sOldIdx; // iterates through sOld
            char sNew_i; // ith character of sNew
            char sOld_j; // jth character of sOld
            int cost; // cost

            /// Test string length
            if (Math.Max(sNew.Length, sOld.Length) > Math.Pow(2, 31))
                throw (new Exception("\nMaximum string length in Levenshtein.LD is " + Math.Pow(2, 31) + ".\nYours is " + Math.Max(sNew.Length, sOld.Length) + "."));

            // Step 1

            if (sNewLen == 0)
            {
                return sOldLen;
            }

            if (sOldLen == 0)
            {
                return sNewLen;
            }

            matrix = new int[sNewLen + 1, sOldLen + 1];

            // Step 2

            for (sNewIdx = 0; sNewIdx <= sNewLen; sNewIdx++)
            {
                matrix[sNewIdx, 0] = sNewIdx;
            }

            for (sOldIdx = 0; sOldIdx <= sOldLen; sOldIdx++)
            {
                matrix[0, sOldIdx] = sOldIdx;
            }

            // Step 3

            for (sNewIdx = 1; sNewIdx <= sNewLen; sNewIdx++)
            {
                sNew_i = sNew[sNewIdx - 1];

                // Step 4

                for (sOldIdx = 1; sOldIdx <= sOldLen; sOldIdx++)
                {
                    sOld_j = sOld[sOldIdx - 1];

                    // Step 5

                    if (sNew_i == sOld_j)
                    {
                        cost = 0;
                    }
                    else
                    {
                        cost = 1;
                    }

                    // Step 6

                    matrix[sNewIdx, sOldIdx] = Minimum(matrix[sNewIdx - 1, sOldIdx] + 1, matrix[sNewIdx, sOldIdx - 1] + 1, matrix[sNewIdx - 1, sOldIdx - 1] + cost);

                }
            }

            // Step 7

            /// Value between 0 - 100
            /// 100==perfect match 0==totaly different
            //System.Console.WriteLine("Dist=" + matrix[sNewLen, sOldLen]);
            int max = Math.Max(sNewLen, sOldLen);
            return Math.Abs(100.0 * (max - matrix[sNewLen, sOldLen])) / max;
        }
    }
}
