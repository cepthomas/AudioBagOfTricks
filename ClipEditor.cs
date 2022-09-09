﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.IO;
using System.Windows.Forms;
using System.Diagnostics;
using System.Drawing.Design;
using System.Text.Json.Serialization;
using System.ComponentModel;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NBagOfTricks;
using NBagOfTricks.Slog;
using NBagOfUis;
using AudioLib;


namespace Wavicler
{
    [ToolboxItem(false), Browsable(false)] // not useable in designer
    public partial class ClipEditor : UserControl
    {
        #region Fields
        /// <summary>The bound input sample provider.</summary>
        ClipSampleProvider _prov = new(Array.Empty<float>());
        #endregion

        #region Properties
        /// <summary>The bound input sample provider.</summary>
        public ISampleProvider SampleProvider { get { return _prov; } }

        /// <summary>Current file.</summary>
        public string FileName { get; private set; } = "";
        #endregion

        #region Properties - mainly pass through to wave viewer
        /// <summary>For styling.</summary>
        public Color DrawColor { set { wvData.DrawColor = value; wvNav.DrawColor = value; } }

        /// <summary>For styling.</summary>
        public Color GridColor { set { wvData.GridColor = value; } }

        /// <summary>Snap control.</summary>
        public bool Snap { set { wvData.Snap = value; } }

        /// <summary>For beat mode.</summary>
        public float BPM { set { wvData.BPM = value; } }

        /// <summary>How to select wave.</summary>
        public WaveSelectionMode SelectionMode { set { wvData.SelectionMode = value; } }

        /// <summary>Gain adjustment.</summary>
        public double Gain { get { return wvData.Gain; } set { wvData.Gain = (float)value; } }
        #endregion

        #region Events
        /// <summary>Ask the owner to do something.</summary>
        public event EventHandler<ServiceRequestEventArgs>? ServiceRequestEvent;
        public enum ServiceRequest { CopySelectionToNewClip, Close }

        public class ServiceRequestEventArgs
        {
            public ServiceRequest Request { get; set; }
        }
        #endregion

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

        }


        #region Lifecycle
        /// <summary>
        /// Normal constructor.
        /// </summary>
        /// <param name="prov">The bound input sample provider.</param>
        public ClipEditor(ClipSampleProvider prov)
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);

            InitializeComponent();

            _prov = prov;

            // Hook up provider and ui.
            wvData.Init(_prov, false);
            wvNav.Init(_prov, true);
            InitSelection();
            wvNav.MarkerChangedEvent += (_, __) => wvData.Center(wvNav.Marker);
            wvData.SelectionChangedEvent += (_, __) => InitSelection();

            contextMenu.Opening += (_, __) =>
            {
                contextMenu.Items.Clear();
                contextMenu.Items.Add("Fit Gain", null, (_, __) => wvData.FitGain());
                contextMenu.Items.Add("Reset Gain", null, (_, __) => wvData.Gain = 1.0f);
                contextMenu.Items.Add("Remove Marker", null, (_, __) => wvData.Marker = 0);
                contextMenu.Items.Add("Copy To New Clip", null, (_, __) =>
                {
                    ServiceRequestEvent?.Invoke(this, new() { Request = ServiceRequest.CopySelectionToNewClip });
                });
                contextMenu.Items.Add("Close", null, (_, __) =>
                {
                    ServiceRequestEvent?.Invoke(this, new() { Request = ServiceRequest.Close });
                });
            };
        }

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                wvData.Dispose();
                wvNav.Dispose();
                components.Dispose();
            }

            base.Dispose(disposing);
        }
        #endregion

        /// <summary>
        /// Helper.
        /// </summary>
        void InitSelection()
        {
            _prov.SelStart = wvData.SelStart;
            _prov.SelLength = wvData.SelLength;
        }
    }
}
