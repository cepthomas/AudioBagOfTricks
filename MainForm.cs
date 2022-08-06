﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using NAudio.Wave;
using AudioLib;
using NBagOfTricks;
using NBagOfTricks.Slog;
using NBagOfUis;
using NAudio.Wave.SampleProviders;
using System.Diagnostics;


// While .NET cannot compete with unmanaged languages for very low latency audio work, it still performs better than many
// people would expect. On a fairly modest PC, you can quite easily mix multiple WAV files together, including pass them
// through various effects and codecs, play back glitch free with a latency of around 50ms.


// When an MDI child form has a MainMenu component(with, usually, a menu structure of menu items) and it is
// opened within an MDI parent form that has a MainMenu component(with, usually, a menu structure of menu items),
// the menu items will merge automatically if you have set the MergeType property(and optionally, the MergeOrder
// property). Set the MergeType property of both MainMenu components and all of the menu items of the child form
// to MergeItems.Additionally, set the MergeOrder property so that the menu items from both menus appear in the
// desired order.Moreover, keep in mind that when you close an MDI parent form, each of the MDI child forms raises
// a Closing event before the Closing event for the MDI parent is raised. Canceling an MDI child's Closing event
// will not prevent the MDI parent's Closing event from being raised; however, the CancelEventArgs argument for
// the MDI parent's Closing event will now be set to true. You can force the MDI parent and all MDI child forms
// to close by setting the CancelEventArgs argument to false.


// public int Channels => channels;
// public int SampleRate => sampleRate;
// public int AverageBytesPerSecond => averageBytesPerSecond;
// public virtual int BlockAlign => blockAlign;
// /// Returns the number of bits per sample (usually 16 or 32, sometimes 24 or 8)
// public int BitsPerSample => bitsPerSample;
// public int ExtraSize => extraSize;
// public WaveFormatEncoding  waveFormatTag

//public override PeakInfo GetNextPeak()
//{
//    var samplesRead = Provider.Read(ReadBuffer,0,ReadBuffer.Length);
//    var max = 0.0f;
//    var min = 0.0f;
//    for (int x = 0; x < samplesRead; x += sampleInterval)
//    {
//        max = Math.Max(max, ReadBuffer[x]);
//        min = Math.Min(min, ReadBuffer[x]);
//    }
//
//    return new PeakInfo(min,max);
//}



namespace Wavicler
{
    public partial class MainForm : Form
    {
        #region Types
        /// <summary>What are we doing.</summary>
        public enum AppState { Stop, Play, Rewind, Complete, Dead }
        #endregion

        #region Fields
        /// <summary>My logger.</summary>
        readonly Logger _logger = LogManager.CreateLogger("MainForm");

        /// <summary>The actual player.</summary>
        readonly AudioPlayer _player;

        /// <summary>Where we be.</summary>
        AppState _currentState = AppState.Stop;

        /// <summary>Stream read chunk.</summary>
        const int READ_BUFF_SIZE = 100000;

        /// <summary>TODO loop?</summary>
        bool _loop = false;

        /// <summary>TODO kludgy?</summary>
        readonly MainToolbar MT = new();

        /// <summary>Dynamically connect input providers to the player.</summary>
        SwappableSampleProvider _swapper = new(WaveFormat.CreateIeeeFloatWaveFormat(44100, 1));
        #endregion

        #region Lifecycle
        public MainForm()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);

            // Must do this first before initializing.
            string appDir = MiscUtils.GetAppDataDir("Wavicler", "Ephemera");
            UserSettings.TheSettings = (UserSettings)Settings.Load(appDir, typeof(UserSettings));
            // Tell the libs about their settings.
            AudioSettings.LibSettings = UserSettings.TheSettings.AudioSettings;

            // Build it.
            InitializeComponent();
            Icon = Properties.Resources.tiger;
            ToolStrip.Items.Add(new ToolStripControlHost(MT));

            // Init logging.
            LogManager.MinLevelFile = UserSettings.TheSettings.FileLogLevel;
            LogManager.MinLevelNotif = UserSettings.TheSettings.NotifLogLevel;
            LogManager.LogEvent += LogManager_LogEvent;
            LogManager.Run();

            // Init main form from settings
            WindowState = FormWindowState.Normal;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(UserSettings.TheSettings.FormGeometry.X, UserSettings.TheSettings.FormGeometry.Y);
            Size = new Size(UserSettings.TheSettings.FormGeometry.Width, UserSettings.TheSettings.FormGeometry.Height);
            KeyPreview = true; // for routing kbd strokes through OnKeyDown


            // The text output. Maybe use OARS style?
            MT.txtInfo.Font = Font;
            MT.txtInfo.WordWrap = true;
            MT.txtInfo.MatchColors.Add("ERR", Color.LightPink);
            MT.txtInfo.MatchColors.Add("WRN", Color.Plum);

            // Create output.
            _player = new(UserSettings.TheSettings.AudioSettings.WavOutDevice, int.Parse(UserSettings.TheSettings.AudioSettings.Latency));
            // Usually end of file but could be error.
            _player.PlaybackStopped += (object? sender, StoppedEventArgs e) =>
            {
                if (e.Exception is not null)
                {
                    _logger.Exception(e.Exception, "Other NAudio error");
                    UpdateState(AppState.Dead);
                }
                else
                {
                    UpdateState(AppState.Complete);
                }
            };

            // Hook up audio processing chain.
            var postVolumeMeter = new MeteringSampleProvider(_swapper, _swapper.WaveFormat.SampleRate / 10);
            postVolumeMeter.StreamVolume += (object? sender, StreamVolumeEventArgs e) =>
            {
                // TODO timeBar.Current = _reader.CurrentTime;
            };
            _player.Init(postVolumeMeter);

            MT.btnRewind.Click += (_, __) => { UpdateState(AppState.Rewind); };
            MT.chkPlay.Click += (_, __) => { UpdateState(MT.chkPlay.Checked ? AppState.Play : AppState.Stop); };
            MT.volumeMaster.ValueChanged += (_, __) => { _player.Volume = (float)MT.volumeMaster.Value; };
            MT.volumeMaster.Minimum = AudioLibDefs.VOLUME_MIN;
            MT.volumeMaster.Maximum = AudioLibDefs.VOLUME_MAX;
            MT.volumeMaster.Value = UserSettings.TheSettings.Volume;

            // Managing files. FileMenuItem
            FileMenuItem.DropDownOpening += Recent_DropDownOpening;
            NewMenuItem.Click += (_, __) => { OpenFile(); };
            OpenMenuItem.Click += (_, __) => { Open(); };
            SaveMenuItem.Click += (_, __) => { SaveFile(ActiveEditor()); };
            SaveAsMenuItem.Click += (_, __) => { SaveFileAs(ActiveEditor()); };
            CloseMenuItem.Click += (_, __) => { Close(false); };
            CloseAllMenuItem.Click += (_, __) => { Close(true); };
            ExitMenuItem.Click += (_, __) => { Close(true); };

            // Editing. EditMenuItem
            CutMenuItem.Click += (_, __) => { Cut(); };
            CopyMenuItem.Click += (_, __) => { Copy(); };
            PasteMenuItem.Click += (_, __) => { Paste(); };
            ReplaceMenuItem.Click += (_, __) => { Replace(); };
            // RemoveEnvelopeMenuItem

            // Tools. ToolsMenuItem
            AboutMenuItem.Click += (_, __) => { MiscUtils.ShowReadme("Wavicler"); };
            SettingsMenuItem.Click += (_, __) => { EditSettings(); };
            // BpmMenuItem

            // Misc events.
            Menu.MenuActivate += (_, __) => { UpdateMenu(); };

            UpdateMenu();

            Text = $"Wavicler {MiscUtils.GetVersionString()}";

            // >>>>>>>>>>>>>>>>>>>
            OpenFile(@"C:\Dev\repos\TestAudioFiles\Cave Ceremony 01.wav");
            //OpenFile(@"C:\Dev\repos\TestAudioFiles\ref-stereo.wav");
        }

        /// <summary>
        /// Form is legal now. Init things that want to log.
        /// </summary>
        /// <param name="e"></param>
        protected override void OnLoad(EventArgs e)
        {
            _logger.Info($"OK to log now!!");

            if (!_player.Valid)
            {
                var s = $"Something wrong with your audio output device:{UserSettings.TheSettings.AudioSettings.WavOutDevice}";
                _logger.Error(s);
                UpdateState(AppState.Dead);
            }
        }

        /// <summary>
        /// Bye-bye.
        /// </summary>
        /// <param name="e"></param>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Close(true);
            LogManager.Stop();
            UpdateState(AppState.Stop);
            SaveSettings();
            base.OnFormClosing(e);
        }

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }

            // My stuff here.
            _player.Run(false);
            _player.Dispose();
            
            base.Dispose(disposing);
        }
        #endregion

        #region State management
        /// <summary>
        /// General state management. Everything goes through here.
        /// </summary>
        void UpdateState(AppState newState)
        {
            // Unhook.
            MT.chkPlay.CheckedChanged -= ChkPlay_CheckedChanged;

            if (newState != _currentState)
            {
                _logger.Info($"State change:{newState}");
            }

            switch (newState)
            {
                case AppState.Complete:
                    Rewind();
                    if (_loop)
                    {
                        Play();
                    }
                    else
                    {
                        Stop();
                    }
                    break;

                case AppState.Play:
                    Play();
                    break;

                case AppState.Stop:
                    Stop();
                    break;

                case AppState.Rewind:
                    Rewind();
                    break;

                case AppState.Dead:
                    Stop();
                    break;
            }

            _currentState = newState;

            // Rehook.
            MT.chkPlay.CheckedChanged += ChkPlay_CheckedChanged;

            // Local funcs
            void Play()
            {
                MT.chkPlay.Checked = true;
                _player.Run(true);
            }

            void Stop()
            {
                MT.chkPlay.Checked = false;
                _player.Run(false);
            }

            void Rewind()
            {
                //if(_reader is not null)
                //{
                //    _reader.Position = 0;
                //}
                //_player.Rewind();
                //timeBar.Current = TimeSpan.Zero;
            }
        }

        /// <summary>
        /// Play button handler.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void ChkPlay_CheckedChanged(object? sender, EventArgs e)
        {
            UpdateState(MT.chkPlay.Checked ? AppState.Play : AppState.Stop);
        }
        #endregion

        #region Menu management
        /// <summary>
        /// Organize the file menu item drop down.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void Recent_DropDownOpening(object? sender, EventArgs e)
        {
            RecentMenuItem.DropDownItems.Clear();

            UserSettings.TheSettings.RecentFiles.ForEach(f =>
            {
                ToolStripMenuItem menuItem = new(f);
                menuItem.Click += (object? sender, EventArgs e) =>
                {
                    string fn = sender!.ToString()!;
                    OpenFile(fn);
                };

                RecentMenuItem.DropDownItems.Add(menuItem);
            });
        }

        /// <summary>
        /// Set menu item enables according to system states.
        /// </summary>
        void UpdateMenu()
        {
            bool anyOpen = false;
            bool dirty = false;
            bool hasClip = false; // TODO

            var child = ActiveEditor();
            if(child is WaveEditor)
            {
                anyOpen = true;
                dirty = child.Dirty;
            }

            MT.btnRewind.Enabled = anyOpen;
            MT.chkPlay.Enabled = anyOpen;
            MT.volumeMaster.Enabled = true;
            NewMenuItem.Enabled = true;
            OpenMenuItem.Enabled = true;
            SaveMenuItem.Enabled = dirty;
            SaveAsMenuItem.Enabled = dirty;
            CloseMenuItem.Enabled = anyOpen;
            CloseAllMenuItem.Enabled = anyOpen;
            ExitMenuItem.Enabled = true;
            CutMenuItem.Enabled = anyOpen;
            CopyMenuItem.Enabled = anyOpen;
            PasteMenuItem.Enabled = hasClip;
            ReplaceMenuItem.Enabled = anyOpen && hasClip;
        }
        #endregion




        /// <summary>
        /// General closer/saver.
        /// </summary>
        /// <param name="all"></param>
        /// <returns></returns>
        bool Close(bool all)
        {
            bool ok = true;

            if(all)
            {
                MdiChildren.Where(ch => ch is WaveEditor).ForEach(ch =>
                {
                    CloseOne(ch as WaveEditor);
                });
            }
            else
            {
                var ed = ActiveEditor();
                if(ed is not null)
                {
                    CloseOne(ed);
                }
            }

            void CloseOne(WaveEditor ed)
            {
                if(ed.Dirty)
                {
                    // TODO ask to save. If yes, save()

                }

                ed.Close();
                ed.Dispose();
            }

            UpdateMenu();

            return ok;
        }


        #region Cut/copy/paste TODO
        void Cut()
        {

        }

        void Copy()
        {

        }

        void Paste()
        {

        }

        void Replace()
        {

        }
        #endregion

        #region File I/O
        /// <summary>
        /// Common file opener.
        /// </summary>
        /// <param name="fn">The file to open or create new if empty.</param>
        /// <returns>Status.</returns>
        bool OpenFile(string fn = "")
        {
            bool ok = false;

            UpdateState(AppState.Stop);

            if(fn == "")
            {
                _logger.Info($"Creating new child");
                WaveEditor childNew = new(Array.Empty<float>(), fn) { MdiParent = this };
                childNew.Show();
                ok = true;
            }
            else
            {
                var ext = Path.GetExtension(fn).ToLower();
                if(!File.Exists(fn))
                {
                    _logger.Error($"Invalid file: {fn}");
                }
                else if (AudioLibDefs.AUDIO_FILE_TYPES.Contains(ext))
                {
                    _logger.Info($"Opening file: {fn}");

                    // Read all data. TODO check reader.WaveFormat that it's +-1.0f
                    var reader = new AudioFileReader(fn);


                    long len = reader.Length / (reader.WaveFormat.BitsPerSample / 8);
                    var data = new float[len];

                    // int offset = 0;
                    // int num = -1;
                    // while (num != 0)
                    // {
                    //     // This throws for flac and m4a files for unknown reason but works ok.
                    //     try
                    //     {
                    //         num = reader.Read(data, offset, READ_BUFF_SIZE);
                    //         offset += num; TODO this won't get executed
                    //     }
                    //     catch (Exception)
                    //     {
                    //     }
                    // }

                    int offset = 0;
                    bool done = false;
                    while (!done)
                    {
                        int toread = READ_BUFF_SIZE;
                        if (len - offset < toread)
                        {
                            toread = (int)len - offset;
                            done = true; // last bunch
                        }
                        offset += reader.Read(data, offset, toread);
                    }

                    if (reader.WaveFormat.Channels == 2) // stereo interleaved
                    {
                        // TODO ask user if they want L/R/both-separate
                        // StereoToMonoSampleProvider to split stereo into 2 mono?

                        long stlen = len / 2;
                        var dataL = new float[stlen];
                        var dataR = new float[stlen];

                        for (long i = 0; i < stlen; i++)
                        {
                            dataL[i] = data[i * 2];
                            dataR[i] = data[i * 2 + 1];
                        }
                        WaveEditor childL = new(dataL, $"{fn}.left") { MdiParent = this };
                        childL.Show();
                        WaveEditor childR = new(dataR, $"{fn}.right") { MdiParent = this };
                        childR.Show();
                    }
                    else // mono
                    {
                        var vals = new float[data.Length];
                        Array.Copy(data, vals, data.Length);
                        WaveEditor childM = new(vals, fn) { MdiParent = this };
                        childM.Show();
                    }
                    ok = true;
                }
                else
                {
                    _logger.Error($"Unsupported file type: {fn}");
                }
            }

            if (ok && UserSettings.TheSettings.Autoplay)
            {
                UpdateState(AppState.Rewind);
                UpdateState(AppState.Play);
            }

            UpdateMenu();

            return ok;
        }

        /// <summary>
        /// Common file saver.
        /// </summary>
        /// <param name="ed">Data source.</param>
        /// <param name="fn">The file to save to.</param>
        /// <returns>Status.</returns>
        bool SaveFile(WaveEditor? ed, string fn = "")
        {
            bool ok = false;

            if(ed is not null)
            {
                // TODO get all rendered data and save to audio file - to fn if specified else old.

            }

            return ok;
        }

        /// <summary>
        /// Allows the user to select an audio clip or midi from file system.
        /// </summary>
        void Open()
        {
            var fileTypes = $"Audio Files|{AudioLibDefs.AUDIO_FILE_TYPES}";
            using OpenFileDialog openDlg = new()
            {
                Filter = fileTypes,
                Title = "Select a file"
            };

            if (openDlg.ShowDialog() == DialogResult.OK)
            {
                OpenFile(openDlg.FileName);
            }
        }

        /// <summary>
        /// Save the file in the current child.
        /// </summary>
        /// <param name="child">Data source.</param>
        void SaveFileAs(WaveEditor? child)
        {
            if(child is WaveEditor)
            {
                using SaveFileDialog saveDlg = new()
                {
                    Filter = AudioLibDefs.AUDIO_FILE_TYPES,
                    Title = "Save as file"
                };

                if (saveDlg.ShowDialog() == DialogResult.OK)
                {
                    SaveFile(child, saveDlg.FileName);
                }
            }
        }
        #endregion

        #region Settings
        /// <summary>
        /// Edit user settings.
        /// </summary>
        void EditSettings()
        {
            var changes = UserSettings.TheSettings.Edit("User Settings", 300);

            // Check for meaningful changes.
            //timeBar.SnapMsec = UserSettings.TheSettings.AudioSettings.SnapMsec;
        }

        /// <summary>
        /// Collect and save user settings.
        /// </summary>
        void SaveSettings()
        {
            UserSettings.TheSettings.FormGeometry = new Rectangle(Location.X, Location.Y, Width, Height);
            UserSettings.TheSettings.Volume = MT.volumeMaster.Value;
            //Common.Settings.Autoplay = btnAutoplay.Checked;
            //Common.Settings.Loop = btnLoop.Checked;
            UserSettings.TheSettings.Save();
        }
        #endregion

        #region Misc stuff
        /// <summary>
        /// Get current focus editor.
        /// </summary>
        /// <returns>The editor or null if invalid.</returns>
        WaveEditor? ActiveEditor()
        {
            WaveEditor? ret = null;

            // Get the form with focus.
            var child = ActiveMdiChild;
            if (child is WaveEditor)
            {
                ret = child as WaveEditor;
            }

            return ret;
        }

        /// <summary>
        /// Do some global key handling. Space bar is used for stop/start playing.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Space:
                    // Toggle.
                    UpdateState(MT.chkPlay.Checked ? AppState.Stop : AppState.Play);
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>
        /// Show log events.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void LogManager_LogEvent(object? sender, LogEventArgs e)
        {
            // Usually come from a different thread.
            if (IsHandleCreated)
            {
                this.InvokeIfRequired(_ => { MT.txtInfo.AppendLine($"{e.Message}"); });
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void MainForm_MdiChildActivate(object sender, EventArgs e)
        {
            var child = ActiveMdiChild;
            _logger.Info($"MDI child:{child.Text}");
        }
        #endregion
    }
}
