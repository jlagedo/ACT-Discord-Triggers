using System;
using System.Text;
using System.Collections.Specialized;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using System.IO;
using System.Reflection;
using ACT_DiscordTriggers.Ipc;
using ACT_DiscordTriggers.ViewModels;
using System.Threading.Tasks;
using System.Drawing;
using System.Diagnostics;
using ACT_DiscordTriggers.Settings;

namespace ACT_DiscordTriggers {
  public class DiscordTriggersView : UserControl {
    #region Designer Created Code (Avoid editing)
    /// <summary>
    /// Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    /// Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing) {
      if (disposing && (components != null)) {
        components.Dispose();
      }
      base.Dispose(disposing);
    }

    #region Component Designer generated code

    /// <summary>
    /// Required method for Designer support - do not modify
    /// the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent() {
      // --- Settings controls (carried over; reparented into the new layout) ---
      this.chkAutoConnect = new System.Windows.Forms.CheckBox();
      this.discordConnectbtn = new System.Windows.Forms.Button();
      this.sliderTTSSpeed = new System.Windows.Forms.TrackBar();
      this.lblTTSSpeed = new System.Windows.Forms.Label();
      this.sliderTTSVol = new System.Windows.Forms.TrackBar();
      this.lblTTSVol = new System.Windows.Forms.Label();
      this.cmbChan = new System.Windows.Forms.ComboBox();
      this.lblChan = new System.Windows.Forms.Label();
      this.cmbServer = new System.Windows.Forms.ComboBox();
      this.lblServer = new System.Windows.Forms.Label();
      this.cmbTTS = new System.Windows.Forms.ComboBox();
      this.lblTTS = new System.Windows.Forms.Label();
      this.btnLeave = new System.Windows.Forms.Button();
      this.btnJoin = new System.Windows.Forms.Button();
      this.lblLog = new System.Windows.Forms.Label();
      this.txtToken = new System.Windows.Forms.TextBox();
      this.lblBotTok = new System.Windows.Forms.Label();
      this.logList = new System.Windows.Forms.ListView();
      this.listColTim = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
      this.listColMsg = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
      this.txtBotStatus = new System.Windows.Forms.TextBox();
      this.lblBotStatus = new System.Windows.Forms.Label();
      this.chkRandomFx = new System.Windows.Forms.CheckBox();
      this.lblFxChance = new System.Windows.Forms.Label();
      this.sliderFxChance = new System.Windows.Forms.TrackBar();
      this.chkNormalize = new System.Windows.Forms.CheckBox();
      this.lblNormalizeTarget = new System.Windows.Forms.Label();
      this.sliderNormalizeTarget = new System.Windows.Forms.TrackBar();
      this.lblAudioQuality = new System.Windows.Forms.Label();
      this.cmbAudioQuality = new System.Windows.Forms.ComboBox();
      this.lblAudioQualityWarn = new System.Windows.Forms.Label();
      // --- New layout containers ---
      this.lstNav = new System.Windows.Forms.ListBox();
      this.pnlContent = new System.Windows.Forms.Panel();
      this.pagGeneral = new System.Windows.Forms.Panel();
      this.pagSound = new System.Windows.Forms.Panel();
      this.pagInfo = new System.Windows.Forms.Panel();
      this.pnlLog = new System.Windows.Forms.Panel();
      this.grpConnection = new System.Windows.Forms.GroupBox();
      this.grpChannel = new System.Windows.Forms.GroupBox();
      this.grpTTS = new System.Windows.Forms.GroupBox();
      this.grpFx = new System.Windows.Forms.GroupBox();
      ((System.ComponentModel.ISupportInitialize)(this.sliderTTSSpeed)).BeginInit();
      ((System.ComponentModel.ISupportInitialize)(this.sliderTTSVol)).BeginInit();
      ((System.ComponentModel.ISupportInitialize)(this.sliderFxChance)).BeginInit();
      ((System.ComponentModel.ISupportInitialize)(this.sliderNormalizeTarget)).BeginInit();
      this.pnlContent.SuspendLayout();
      this.pagGeneral.SuspendLayout();
      this.pagSound.SuspendLayout();
      this.pagInfo.SuspendLayout();
      this.pnlLog.SuspendLayout();
      this.grpConnection.SuspendLayout();
      this.grpChannel.SuspendLayout();
      this.grpTTS.SuspendLayout();
      this.grpFx.SuspendLayout();
      this.SuspendLayout();
      //
      // lblBotTok
      //
      this.lblBotTok.AutoSize = true;
      this.lblBotTok.Location = new System.Drawing.Point(15, 28);
      this.lblBotTok.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
      this.lblBotTok.Name = "lblBotTok";
      this.lblBotTok.Size = new System.Drawing.Size(140, 20);
      this.lblBotTok.TabIndex = 0;
      this.lblBotTok.Text = "Discord Bot Token";
      //
      // txtToken
      //
      this.txtToken.Location = new System.Drawing.Point(18, 50);
      this.txtToken.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
      this.txtToken.Name = "txtToken";
      this.txtToken.Size = new System.Drawing.Size(320, 26);
      this.txtToken.TabIndex = 1;
      this.txtToken.UseSystemPasswordChar = true;
      //
      // discordConnectbtn
      //
      this.discordConnectbtn.Location = new System.Drawing.Point(18, 90);
      this.discordConnectbtn.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
      this.discordConnectbtn.Name = "discordConnectbtn";
      this.discordConnectbtn.Size = new System.Drawing.Size(140, 35);
      this.discordConnectbtn.TabIndex = 2;
      this.discordConnectbtn.Text = "Connect";
      this.discordConnectbtn.UseVisualStyleBackColor = true;
      this.discordConnectbtn.Click += new System.EventHandler(this.discordConnectbtn_Click);
      //
      // chkAutoConnect
      //
      this.chkAutoConnect.AutoSize = true;
      this.chkAutoConnect.Location = new System.Drawing.Point(170, 96);
      this.chkAutoConnect.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
      this.chkAutoConnect.Name = "chkAutoConnect";
      this.chkAutoConnect.Size = new System.Drawing.Size(133, 24);
      this.chkAutoConnect.TabIndex = 3;
      this.chkAutoConnect.Text = "Auto Connect";
      this.chkAutoConnect.UseVisualStyleBackColor = true;
      //
      // lblBotStatus
      //
      this.lblBotStatus.AutoSize = true;
      this.lblBotStatus.Location = new System.Drawing.Point(15, 140);
      this.lblBotStatus.Name = "lblBotStatus";
      this.lblBotStatus.Size = new System.Drawing.Size(85, 20);
      this.lblBotStatus.TabIndex = 4;
      this.lblBotStatus.Text = "Bot Status";
      //
      // txtBotStatus
      //
      this.txtBotStatus.Location = new System.Drawing.Point(18, 162);
      this.txtBotStatus.Name = "txtBotStatus";
      this.txtBotStatus.Size = new System.Drawing.Size(320, 26);
      this.txtBotStatus.TabIndex = 5;
      this.txtBotStatus.Text = "Playing with ACT Triggers";
      //
      // grpConnection
      //
      this.grpConnection.Controls.Add(this.lblBotTok);
      this.grpConnection.Controls.Add(this.txtToken);
      this.grpConnection.Controls.Add(this.discordConnectbtn);
      this.grpConnection.Controls.Add(this.chkAutoConnect);
      this.grpConnection.Controls.Add(this.lblBotStatus);
      this.grpConnection.Controls.Add(this.txtBotStatus);
      this.grpConnection.Location = new System.Drawing.Point(10, 10);
      this.grpConnection.Name = "grpConnection";
      this.grpConnection.Size = new System.Drawing.Size(560, 205);
      this.grpConnection.TabIndex = 0;
      this.grpConnection.TabStop = false;
      this.grpConnection.Text = "Discord Connection";
      //
      // lblServer
      //
      this.lblServer.AutoSize = true;
      this.lblServer.Location = new System.Drawing.Point(15, 28);
      this.lblServer.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
      this.lblServer.Name = "lblServer";
      this.lblServer.Size = new System.Drawing.Size(55, 20);
      this.lblServer.TabIndex = 0;
      this.lblServer.Text = "Server";
      //
      // cmbServer
      //
      this.cmbServer.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
      this.cmbServer.FormattingEnabled = true;
      this.cmbServer.Location = new System.Drawing.Point(18, 50);
      this.cmbServer.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
      this.cmbServer.Name = "cmbServer";
      this.cmbServer.Size = new System.Drawing.Size(320, 28);
      this.cmbServer.TabIndex = 1;
      //
      // lblChan
      //
      this.lblChan.AutoSize = true;
      this.lblChan.Location = new System.Drawing.Point(15, 92);
      this.lblChan.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
      this.lblChan.Name = "lblChan";
      this.lblChan.Size = new System.Drawing.Size(68, 20);
      this.lblChan.TabIndex = 2;
      this.lblChan.Text = "Channel";
      //
      // cmbChan
      //
      this.cmbChan.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
      this.cmbChan.FormattingEnabled = true;
      this.cmbChan.Location = new System.Drawing.Point(18, 114);
      this.cmbChan.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
      this.cmbChan.Name = "cmbChan";
      this.cmbChan.Size = new System.Drawing.Size(320, 28);
      this.cmbChan.TabIndex = 3;
      //
      // btnJoin
      //
      this.btnJoin.Enabled = false;
      this.btnJoin.Location = new System.Drawing.Point(18, 152);
      this.btnJoin.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
      this.btnJoin.Name = "btnJoin";
      this.btnJoin.Size = new System.Drawing.Size(140, 35);
      this.btnJoin.TabIndex = 4;
      this.btnJoin.Text = "Join Channel";
      this.btnJoin.UseVisualStyleBackColor = true;
      this.btnJoin.Click += new System.EventHandler(this.btnJoin_Click);
      //
      // btnLeave
      //
      this.btnLeave.Enabled = false;
      this.btnLeave.Location = new System.Drawing.Point(170, 152);
      this.btnLeave.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
      this.btnLeave.Name = "btnLeave";
      this.btnLeave.Size = new System.Drawing.Size(141, 35);
      this.btnLeave.TabIndex = 5;
      this.btnLeave.Text = "Leave Channel";
      this.btnLeave.UseVisualStyleBackColor = true;
      this.btnLeave.Click += new System.EventHandler(this.btnLeave_Click);
      //
      // grpChannel
      //
      this.grpChannel.Controls.Add(this.lblServer);
      this.grpChannel.Controls.Add(this.cmbServer);
      this.grpChannel.Controls.Add(this.lblChan);
      this.grpChannel.Controls.Add(this.cmbChan);
      this.grpChannel.Controls.Add(this.btnJoin);
      this.grpChannel.Controls.Add(this.btnLeave);
      this.grpChannel.Location = new System.Drawing.Point(10, 225);
      this.grpChannel.Name = "grpChannel";
      this.grpChannel.Size = new System.Drawing.Size(560, 205);
      this.grpChannel.TabIndex = 1;
      this.grpChannel.TabStop = false;
      this.grpChannel.Text = "Voice Channel";
      //
      // pagGeneral
      //
      this.pagGeneral.AutoScroll = true;
      this.pagGeneral.Controls.Add(this.grpConnection);
      this.pagGeneral.Controls.Add(this.grpChannel);
      this.pagGeneral.Dock = System.Windows.Forms.DockStyle.Fill;
      this.pagGeneral.Name = "pagGeneral";
      this.pagGeneral.TabIndex = 0;
      //
      // lblTTS
      //
      this.lblTTS.AutoSize = true;
      this.lblTTS.Location = new System.Drawing.Point(15, 28);
      this.lblTTS.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
      this.lblTTS.Name = "lblTTS";
      this.lblTTS.Size = new System.Drawing.Size(82, 20);
      this.lblTTS.TabIndex = 0;
      this.lblTTS.Text = "TTS Voice";
      //
      // cmbTTS
      //
      this.cmbTTS.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
      this.cmbTTS.FormattingEnabled = true;
      this.cmbTTS.Location = new System.Drawing.Point(18, 50);
      this.cmbTTS.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
      this.cmbTTS.Name = "cmbTTS";
      this.cmbTTS.Size = new System.Drawing.Size(320, 28);
      this.cmbTTS.TabIndex = 1;
      //
      // lblTTSVol
      //
      this.lblTTSVol.AutoSize = true;
      this.lblTTSVol.Location = new System.Drawing.Point(15, 90);
      this.lblTTSVol.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
      this.lblTTSVol.Name = "lblTTSVol";
      this.lblTTSVol.Size = new System.Drawing.Size(96, 20);
      this.lblTTSVol.TabIndex = 2;
      this.lblTTSVol.Text = "TTS Volume";
      //
      // sliderTTSVol
      //
      this.sliderTTSVol.AutoSize = false;
      this.sliderTTSVol.Location = new System.Drawing.Point(18, 112);
      this.sliderTTSVol.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
      this.sliderTTSVol.Maximum = 20;
      this.sliderTTSVol.Name = "sliderTTSVol";
      this.sliderTTSVol.Size = new System.Drawing.Size(320, 35);
      this.sliderTTSVol.TabIndex = 3;
      this.sliderTTSVol.TickStyle = System.Windows.Forms.TickStyle.None;
      this.sliderTTSVol.Value = 10;
      //
      // lblTTSSpeed
      //
      this.lblTTSSpeed.AutoSize = true;
      this.lblTTSSpeed.Location = new System.Drawing.Point(15, 165);
      this.lblTTSSpeed.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
      this.lblTTSSpeed.Name = "lblTTSSpeed";
      this.lblTTSSpeed.Size = new System.Drawing.Size(89, 20);
      this.lblTTSSpeed.TabIndex = 4;
      this.lblTTSSpeed.Text = "TTS Speed";
      //
      // sliderTTSSpeed
      //
      this.sliderTTSSpeed.AutoSize = false;
      this.sliderTTSSpeed.Location = new System.Drawing.Point(18, 187);
      this.sliderTTSSpeed.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
      this.sliderTTSSpeed.Maximum = 20;
      this.sliderTTSSpeed.Name = "sliderTTSSpeed";
      this.sliderTTSSpeed.Size = new System.Drawing.Size(320, 35);
      this.sliderTTSSpeed.TabIndex = 5;
      this.sliderTTSSpeed.TickStyle = System.Windows.Forms.TickStyle.None;
      this.sliderTTSSpeed.Value = 10;
      //
      // grpTTS
      //
      this.grpTTS.Controls.Add(this.lblTTS);
      this.grpTTS.Controls.Add(this.cmbTTS);
      this.grpTTS.Controls.Add(this.lblTTSVol);
      this.grpTTS.Controls.Add(this.sliderTTSVol);
      this.grpTTS.Controls.Add(this.lblTTSSpeed);
      this.grpTTS.Controls.Add(this.sliderTTSSpeed);
      this.grpTTS.Location = new System.Drawing.Point(10, 10);
      this.grpTTS.Name = "grpTTS";
      this.grpTTS.Size = new System.Drawing.Size(560, 240);
      this.grpTTS.TabIndex = 0;
      this.grpTTS.TabStop = false;
      this.grpTTS.Text = "Text-to-Speech";
      //
      // chkRandomFx
      //
      this.chkRandomFx.AutoSize = true;
      this.chkRandomFx.Location = new System.Drawing.Point(18, 25);
      this.chkRandomFx.Name = "chkRandomFx";
      this.chkRandomFx.Size = new System.Drawing.Size(160, 24);
      this.chkRandomFx.TabIndex = 0;
      this.chkRandomFx.Text = "Random Sound FX";
      this.chkRandomFx.UseVisualStyleBackColor = true;
      //
      // lblFxChance
      //
      this.lblFxChance.AutoSize = true;
      this.lblFxChance.Location = new System.Drawing.Point(15, 58);
      this.lblFxChance.Name = "lblFxChance";
      this.lblFxChance.Size = new System.Drawing.Size(85, 20);
      this.lblFxChance.TabIndex = 1;
      this.lblFxChance.Text = "FX Chance";
      //
      // sliderFxChance
      //
      this.sliderFxChance.AutoSize = false;
      this.sliderFxChance.Location = new System.Drawing.Point(18, 80);
      this.sliderFxChance.Maximum = 100;
      this.sliderFxChance.Name = "sliderFxChance";
      this.sliderFxChance.Size = new System.Drawing.Size(320, 35);
      this.sliderFxChance.TabIndex = 2;
      this.sliderFxChance.TickStyle = System.Windows.Forms.TickStyle.None;
      this.sliderFxChance.Value = 25;
      //
      // chkNormalize
      //
      this.chkNormalize.AutoSize = true;
      this.chkNormalize.Checked = true;
      this.chkNormalize.CheckState = System.Windows.Forms.CheckState.Checked;
      this.chkNormalize.Location = new System.Drawing.Point(18, 133);
      this.chkNormalize.Name = "chkNormalize";
      this.chkNormalize.Size = new System.Drawing.Size(180, 24);
      this.chkNormalize.TabIndex = 3;
      this.chkNormalize.Text = "Auto-level Volume";
      this.chkNormalize.UseVisualStyleBackColor = true;
      //
      // lblNormalizeTarget
      //
      this.lblNormalizeTarget.AutoSize = true;
      this.lblNormalizeTarget.Location = new System.Drawing.Point(15, 166);
      this.lblNormalizeTarget.Name = "lblNormalizeTarget";
      this.lblNormalizeTarget.Size = new System.Drawing.Size(120, 20);
      this.lblNormalizeTarget.TabIndex = 4;
      this.lblNormalizeTarget.Text = "Auto-level Target";
      //
      // sliderNormalizeTarget
      //
      this.sliderNormalizeTarget.AutoSize = false;
      this.sliderNormalizeTarget.Location = new System.Drawing.Point(18, 188);
      this.sliderNormalizeTarget.Minimum = 12;
      this.sliderNormalizeTarget.Maximum = 30;
      this.sliderNormalizeTarget.Name = "sliderNormalizeTarget";
      this.sliderNormalizeTarget.Size = new System.Drawing.Size(320, 35);
      this.sliderNormalizeTarget.TabIndex = 5;
      this.sliderNormalizeTarget.TickStyle = System.Windows.Forms.TickStyle.None;
      this.sliderNormalizeTarget.Value = 20;
      //
      // lblAudioQuality
      //
      this.lblAudioQuality.AutoSize = true;
      this.lblAudioQuality.Location = new System.Drawing.Point(15, 235);
      this.lblAudioQuality.Name = "lblAudioQuality";
      this.lblAudioQuality.Size = new System.Drawing.Size(110, 20);
      this.lblAudioQuality.TabIndex = 6;
      this.lblAudioQuality.Text = "Audio Quality";
      //
      // cmbAudioQuality
      //
      this.cmbAudioQuality.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
      this.cmbAudioQuality.FormattingEnabled = true;
      this.cmbAudioQuality.Items.AddRange(new object[] {
        "Low (48 kbps)",
        "Medium (96 kbps)",
        "High (128 kbps)"});
      this.cmbAudioQuality.Location = new System.Drawing.Point(18, 258);
      this.cmbAudioQuality.Name = "cmbAudioQuality";
      this.cmbAudioQuality.Size = new System.Drawing.Size(200, 28);
      this.cmbAudioQuality.TabIndex = 7;
      this.cmbAudioQuality.SelectedIndex = 1;
      //
      // lblAudioQualityWarn
      //
      this.lblAudioQualityWarn.AutoSize = true;
      this.lblAudioQualityWarn.ForeColor = System.Drawing.Color.Firebrick;
      this.lblAudioQualityWarn.Location = new System.Drawing.Point(18, 290);
      this.lblAudioQualityWarn.Name = "lblAudioQualityWarn";
      this.lblAudioQualityWarn.Size = new System.Drawing.Size(400, 20);
      this.lblAudioQualityWarn.TabIndex = 8;
      this.lblAudioQualityWarn.Text = "High needs a Discord channel that supports 128 kbps (boosted server).";
      this.lblAudioQualityWarn.Visible = false;
      //
      // grpFx
      //
      this.grpFx.Controls.Add(this.chkRandomFx);
      this.grpFx.Controls.Add(this.lblFxChance);
      this.grpFx.Controls.Add(this.sliderFxChance);
      this.grpFx.Controls.Add(this.chkNormalize);
      this.grpFx.Controls.Add(this.lblNormalizeTarget);
      this.grpFx.Controls.Add(this.sliderNormalizeTarget);
      this.grpFx.Controls.Add(this.lblAudioQuality);
      this.grpFx.Controls.Add(this.cmbAudioQuality);
      this.grpFx.Controls.Add(this.lblAudioQualityWarn);
      this.grpFx.Location = new System.Drawing.Point(10, 260);
      this.grpFx.Name = "grpFx";
      this.grpFx.Size = new System.Drawing.Size(560, 330);
      this.grpFx.TabIndex = 1;
      this.grpFx.TabStop = false;
      this.grpFx.Text = "Effects && Leveling";
      //
      // pagSound
      //
      this.pagSound.AutoScroll = true;
      this.pagSound.Controls.Add(this.grpTTS);
      this.pagSound.Controls.Add(this.grpFx);
      this.pagSound.Dock = System.Windows.Forms.DockStyle.Fill;
      this.pagSound.Name = "pagSound";
      this.pagSound.TabIndex = 0;
      this.pagSound.Visible = false;
      //
      // pagInfo (contents built programmatically in PopulateInfoPage)
      //
      this.pagInfo.AutoScroll = true;
      this.pagInfo.Dock = System.Windows.Forms.DockStyle.Fill;
      this.pagInfo.Name = "pagInfo";
      this.pagInfo.Padding = new System.Windows.Forms.Padding(10);
      this.pagInfo.TabIndex = 0;
      this.pagInfo.Visible = false;
      //
      // pnlContent
      //
      this.pnlContent.Controls.Add(this.pagGeneral);
      this.pnlContent.Controls.Add(this.pagSound);
      this.pnlContent.Controls.Add(this.pagInfo);
      this.pnlContent.Dock = System.Windows.Forms.DockStyle.Fill;
      this.pnlContent.Name = "pnlContent";
      this.pnlContent.TabIndex = 1;
      //
      // lstNav
      //
      this.lstNav.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
      this.lstNav.Dock = System.Windows.Forms.DockStyle.Left;
      this.lstNav.Font = new System.Drawing.Font("Segoe UI", 11F);
      this.lstNav.FormattingEnabled = true;
      this.lstNav.IntegralHeight = false;
      this.lstNav.ItemHeight = 28;
      this.lstNav.Items.AddRange(new object[] { "General", "Sound", "Information" });
      this.lstNav.Name = "lstNav";
      this.lstNav.Size = new System.Drawing.Size(150, 520);
      this.lstNav.TabIndex = 0;
      this.lstNav.SelectedIndexChanged += new System.EventHandler(this.nav_SelectedIndexChanged);
      //
      // lblLog
      //
      this.lblLog.AutoSize = true;
      this.lblLog.Dock = System.Windows.Forms.DockStyle.Top;
      this.lblLog.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
      this.lblLog.Name = "lblLog";
      this.lblLog.Padding = new System.Windows.Forms.Padding(4, 4, 0, 2);
      this.lblLog.Size = new System.Drawing.Size(88, 26);
      this.lblLog.TabIndex = 0;
      this.lblLog.Text = "Debug Log";
      //
      // logList
      //
      this.logList.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this.listColTim,
            this.listColMsg});
      this.logList.Dock = System.Windows.Forms.DockStyle.Fill;
      this.logList.FullRowSelect = true;
      this.logList.HideSelection = false;
      this.logList.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
      this.logList.Name = "logList";
      this.logList.Size = new System.Drawing.Size(607, 184);
      this.logList.TabIndex = 1;
      this.logList.UseCompatibleStateImageBehavior = false;
      this.logList.View = System.Windows.Forms.View.Details;
      this.logList.KeyUp += new System.Windows.Forms.KeyEventHandler(this.LogList_KeyUp);
      //
      // listColTim
      //
      this.listColTim.Text = "Timestamp";
      this.listColTim.Width = 120;
      //
      // listColMsg
      //
      this.listColMsg.Text = "Message";
      this.listColMsg.Width = 315;
      //
      // pnlLog
      //
      this.pnlLog.Controls.Add(this.logList);
      this.pnlLog.Controls.Add(this.lblLog);
      this.pnlLog.Dock = System.Windows.Forms.DockStyle.Bottom;
      this.pnlLog.Name = "pnlLog";
      this.pnlLog.Size = new System.Drawing.Size(759, 210);
      this.pnlLog.TabIndex = 2;
      //
      // DiscordTriggersView
      //
      this.AutoScaleDimensions = new System.Drawing.SizeF(9F, 20F);
      this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
      // Docking is laid out in reverse z-order: add Fill first (laid out last,
      // fills the leftover), then the left nav, then the full-width bottom log.
      this.Controls.Add(this.pnlContent);
      this.Controls.Add(this.lstNav);
      this.Controls.Add(this.pnlLog);
      this.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
      this.Name = "DiscordTriggersView";
      this.Size = new System.Drawing.Size(759, 730);
      ((System.ComponentModel.ISupportInitialize)(this.sliderTTSSpeed)).EndInit();
      ((System.ComponentModel.ISupportInitialize)(this.sliderTTSVol)).EndInit();
      ((System.ComponentModel.ISupportInitialize)(this.sliderFxChance)).EndInit();
      ((System.ComponentModel.ISupportInitialize)(this.sliderNormalizeTarget)).EndInit();
      this.grpConnection.ResumeLayout(false);
      this.grpConnection.PerformLayout();
      this.grpChannel.ResumeLayout(false);
      this.grpChannel.PerformLayout();
      this.grpTTS.ResumeLayout(false);
      this.grpTTS.PerformLayout();
      this.grpFx.ResumeLayout(false);
      this.grpFx.PerformLayout();
      this.pagGeneral.ResumeLayout(false);
      this.pagSound.ResumeLayout(false);
      this.pagInfo.ResumeLayout(false);
      this.pnlContent.ResumeLayout(false);
      this.pnlLog.ResumeLayout(false);
      this.pnlLog.PerformLayout();
      this.ResumeLayout(false);
      this.PerformLayout();
    }

    #endregion

    #endregion

    #region Init Variables
    FormActMain.PlayTtsDelegate oldTTS;
    FormActMain.PlaySoundDelegate oldSound;
    private DiscordTriggersViewModel vm;
    private DiscordClientService discordService;
    private CheckBox chkAutoConnect;
    private Button discordConnectbtn;
    private TrackBar sliderTTSSpeed;
    private Label lblTTSSpeed;
    private TrackBar sliderTTSVol;
    private Label lblTTSVol;
    private ComboBox cmbChan;
    private Label lblChan;
    private ComboBox cmbServer;
    private Label lblServer;
    private ComboBox cmbTTS;
    private Label lblTTS;
    private Button btnLeave;
    private Button btnJoin;
    private Label lblLog;
    private TextBox txtToken;
    private ListView logList;
    private ColumnHeader listColTim;
    private ColumnHeader listColMsg;
    private TextBox txtBotStatus;
    private Label lblBotStatus;
    private CheckBox chkRandomFx;
    private Label lblFxChance;
    private TrackBar sliderFxChance;
    private CheckBox chkNormalize;
    private Label lblNormalizeTarget;
    private TrackBar sliderNormalizeTarget;
    private Label lblAudioQuality;
    private ComboBox cmbAudioQuality;
    private Label lblAudioQualityWarn;
    private Label lblBotTok;
    // Layout containers for the categorized (nav + paged) settings UI.
    private ListBox lstNav;
    private Panel pnlContent;
    private Panel pagGeneral;
    private Panel pagSound;
    private Panel pagInfo;
    private Panel pnlLog;
    private GroupBox grpConnection;
    private GroupBox grpChannel;
    private GroupBox grpTTS;
    private GroupBox grpFx;
    // Mirror the VM's ObservableCollections into BindingLists the combos can observe
    // (WinForms binding ignores INotifyCollectionChanged); disposed on deinit.
    private ObservableBindingList<string> voicesBinding;
    private ObservableBindingList<string> serversBinding;
    private ObservableBindingList<string> channelsBinding;
    #endregion

    public DiscordTriggersView() {
      //Load UI Components and Assemblies
      InitializeComponent();
      PopulateInfoPage();

      //Show the first page (also makes the nav selection visible).
      lstNav.SelectedIndex = 0;
    }

    #region Plugin lifecycle (driven by the DiscordTriggersPlugin host)
    // Called by the host after it has set the bridge path and initialised the
    // diagnostics log. View-level init only: ACT delegates, settings, bot wiring.
    public void OnPluginInit(string configName) {
      //ACT delegates (restored on leave / deinit); save before any hook swap.
      oldTTS = ActGlobals.oFormActMain.PlayTtsMethod;
      oldSound = ActGlobals.oFormActMain.PlaySoundMethod;

      // Build the ViewModel over the production Discord adapter + settings store.
      // Diagnostics is initialised by the host (BEFORE us, so the one-time config
      // migration in Load lands in the file). The store's log sink forwards to the
      // VM so migration messages still surface in the UI log.
      discordService = new DiscordClientService();
      string configDir = Path.Combine(ActGlobals.oFormActMain.AppDataFolder.FullName, "Config");
      var store = new SettingsStore(configDir, $"{configName}.config.xml", msg => vm?.Log(msg));
      vm = new DiscordTriggersViewModel(discordService, store);

      BindControls();

      // The VM stays ACT-free: it raises these so the view swaps ACT's TTS/sound
      // delegates to route through Discord while joined.
      vm.JoinedChannel += OnJoinedChannel;
      vm.LeftChannel += OnLeftChannel;
      vm.LogEntries.CollectionChanged += OnLogEntriesChanged;

      vm.Log("Diagnostics log: " + DiagnosticsLog.UnifiedPath);
      vm.Initialize();
    }

    public async Task OnPluginDeInitAsync() {
      ActGlobals.oFormActMain.PlayTtsMethod = oldTTS;
      ActGlobals.oFormActMain.PlaySoundMethod = oldSound;
      if (vm != null) {
        vm.JoinedChannel -= OnJoinedChannel;
        vm.LeftChannel -= OnLeftChannel;
        vm.LogEntries.CollectionChanged -= OnLogEntriesChanged;
        vm.Save();
        try {
          await vm.ShutdownAsync();
        } catch (Exception ex) {
          ActGlobals.oFormActMain.WriteExceptionLog(ex, "Error with DeInit of Discord Plugin.");
        }
      }
      discordService?.Dispose();
      voicesBinding?.Dispose();
      serversBinding?.Dispose();
      channelsBinding?.Dispose();
    }
    #endregion

    #region View <-> ViewModel wiring
    // Two-way DataBindings (OnPropertyChanged) for scalar controls; one-way for
    // labels/enabled state; an ObservableBindingList mirror for the VM's
    // ObservableCollections so the combos refresh as the VM repopulates them.
    private void BindControls() {
      txtToken.DataBindings.Add("Text", vm, nameof(vm.BotToken), false, DataSourceUpdateMode.OnPropertyChanged);
      txtBotStatus.DataBindings.Add("Text", vm, nameof(vm.BotStatus), false, DataSourceUpdateMode.OnPropertyChanged);
      chkAutoConnect.DataBindings.Add("Checked", vm, nameof(vm.AutoConnect), false, DataSourceUpdateMode.OnPropertyChanged);
      sliderTTSVol.DataBindings.Add("Value", vm, nameof(vm.TtsVolume), false, DataSourceUpdateMode.OnPropertyChanged);
      sliderTTSSpeed.DataBindings.Add("Value", vm, nameof(vm.TtsSpeed), false, DataSourceUpdateMode.OnPropertyChanged);
      chkRandomFx.DataBindings.Add("Checked", vm, nameof(vm.RandomFx), false, DataSourceUpdateMode.OnPropertyChanged);
      sliderFxChance.DataBindings.Add("Value", vm, nameof(vm.FxChance), false, DataSourceUpdateMode.OnPropertyChanged);
      chkNormalize.DataBindings.Add("Checked", vm, nameof(vm.Normalize), false, DataSourceUpdateMode.OnPropertyChanged);
      sliderNormalizeTarget.DataBindings.Add("Value", vm, nameof(vm.NormalizeTarget), false, DataSourceUpdateMode.OnPropertyChanged);
      cmbAudioQuality.DataBindings.Add("SelectedIndex", vm, nameof(vm.AudioQualityIndex), false, DataSourceUpdateMode.OnPropertyChanged);

      lblFxChance.DataBindings.Add("Text", vm, nameof(vm.FxChanceLabel), false, DataSourceUpdateMode.Never);
      lblNormalizeTarget.DataBindings.Add("Text", vm, nameof(vm.NormalizeTargetLabel), false, DataSourceUpdateMode.Never);
      lblAudioQualityWarn.DataBindings.Add("Visible", vm, nameof(vm.ShowHighQualityWarning), false, DataSourceUpdateMode.Never);
      btnJoin.DataBindings.Add("Enabled", vm, nameof(vm.CanJoin), false, DataSourceUpdateMode.Never);
      btnLeave.DataBindings.Add("Enabled", vm, nameof(vm.CanLeave), false, DataSourceUpdateMode.Never);

      voicesBinding = new ObservableBindingList<string>(vm.Voices);
      cmbTTS.DataSource = voicesBinding;
      cmbTTS.DataBindings.Add("SelectedItem", vm, nameof(vm.TtsVoice), true, DataSourceUpdateMode.OnPropertyChanged);
      serversBinding = new ObservableBindingList<string>(vm.Servers);
      cmbServer.DataSource = serversBinding;
      cmbServer.DataBindings.Add("SelectedItem", vm, nameof(vm.SelectedServer), true, DataSourceUpdateMode.OnPropertyChanged);
      channelsBinding = new ObservableBindingList<string>(vm.Channels);
      cmbChan.DataSource = channelsBinding;
      cmbChan.DataBindings.Add("SelectedItem", vm, nameof(vm.SelectedChannel), true, DataSourceUpdateMode.OnPropertyChanged);
    }

    private void OnJoinedChannel() {
      ActGlobals.oFormActMain.PlayTtsMethod = vm.SpeakText;
      ActGlobals.oFormActMain.PlaySoundMethod = vm.SpeakSoundFile;
    }

    private void OnLeftChannel() {
      ActGlobals.oFormActMain.PlayTtsMethod = oldTTS;
      ActGlobals.oFormActMain.PlaySoundMethod = oldSound;
    }

    // The VM marshals collection mutations to the captured UI context, so this fires
    // on the UI thread; the InvokeRequired guard is belt-and-suspenders.
    private void OnLogEntriesChanged(object sender, NotifyCollectionChangedEventArgs e) {
      if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems == null) return;
      if (InvokeRequired) { BeginInvoke(new Action(() => OnLogEntriesChanged(sender, e))); return; }
      foreach (LogEntry entry in e.NewItems) {
        logList.Items.Add(new ListViewItem(new[] {
          entry.Timestamp.ToShortDateString() + " " + entry.Timestamp.ToLongTimeString(),
          entry.Message
        }));
      }
    }
    #endregion

    #region UI Events
    private void nav_SelectedIndexChanged(object sender, EventArgs e) {
      ShowPage(lstNav.SelectedIndex);
    }

    // Swap the visible content page. Guarded because the ListBox selection can
    // change before the page panels exist during construction.
    private void ShowPage(int index) {
      if (pagGeneral == null) return;
      pagGeneral.Visible = index == 0;
      pagSound.Visible = index == 1;
      pagInfo.Visible = index == 2;
    }

    // Buttons forward to the VM commands (server/channel selection + state live in
    // the VM via DataBindings).
    private void discordConnectbtn_Click(object sender, EventArgs e) => vm.ConnectCommand.Execute(null);
    private void btnJoin_Click(object sender, EventArgs e) => vm.JoinCommand.Execute(null);
    private void btnLeave_Click(object sender, EventArgs e) => vm.LeaveCommand.Execute(null);

    private void LogList_KeyUp(object sender, KeyEventArgs e) {
      if (sender != logList)
        return;

      if (e.Control && e.KeyCode == Keys.C && logList.SelectedItems.Count > 0) {
        var builder = new StringBuilder();
        foreach (ListViewItem item in logList.SelectedItems)
          builder.AppendLine(item.SubItems[1].Text);

        string clipboard = builder.ToString();
        if (clipboard.Length > 0)
          Clipboard.SetText(builder.ToString());
      }
    }

    #endregion

    #region Information page
    private const string RepoUrl = "https://github.com/jlagedo/ACT-Discord-Triggers";
    private const string IssuesUrl = "https://github.com/jlagedo/ACT-Discord-Triggers/issues/new";
    private const string SetupGuideUrl =
      "https://github.com/jlagedo/ACT-Discord-Triggers/wiki/First-Time-Setup-Guide";

    // User-focused "About" page: mascot, name + version, what it does, the links a
    // user actually needs (project, bug report, setup), a one-click path to the
    // diagnostics log for bug reports, and the legal disclaimer. Built in code
    // (not the designer) because the logo and links are loaded/wired at runtime.
    private void PopulateInfoPage() {
      pagInfo.SuspendLayout();
      pagInfo.Controls.Clear();

      var root = new TableLayoutPanel {
        Dock = DockStyle.Top,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 1,
        Padding = new Padding(0, 8, 0, 12),
      };
      root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

      var logo = new PictureBox {
        Size = new Size(112, 112),
        SizeMode = PictureBoxSizeMode.Zoom,
        Anchor = AnchorStyles.None,
        Margin = new Padding(0, 0, 0, 6),
      };
      var img = LoadLogo();
      if (img != null) logo.Image = img; else logo.Visible = false;

      var title = new Label {
        Text = "ACT Discord Triggers",
        Font = new Font("Segoe UI", 15f, FontStyle.Bold),
        AutoSize = true,
        Anchor = AnchorStyles.None,
        Margin = new Padding(0),
      };

      var version = new Label {
        Text = "v" + DiscordTriggersPlugin.PluginVersion(),
        Font = new Font("Segoe UI", 9f),
        ForeColor = SystemColors.GrayText,
        AutoSize = true,
        Anchor = AnchorStyles.None,
        Margin = new Padding(0, 0, 0, 8),
      };

      var tagline = new Label {
        Text = "Play your ACT triggers — text-to-speech and sound effects —\n"
             + "through a Discord voice bot, so your whole party hears them.",
        Font = new Font("Segoe UI", 9.5f),
        TextAlign = ContentAlignment.MiddleCenter,
        AutoSize = true,
        Anchor = AnchorStyles.None,
        Margin = new Padding(0, 0, 0, 12),
      };

      var links = new FlowLayoutPanel {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Anchor = AnchorStyles.None,
        Margin = new Padding(0, 0, 0, 12),
      };
      links.Controls.Add(MakeLink("↗  Project on GitHub", RepoUrl));
      links.Controls.Add(MakeLink("↗  Report a problem / open an issue", IssuesUrl));
      links.Controls.Add(MakeLink("↗  First-Time Setup Guide", SetupGuideUrl));

      var diagLabel = new Label {
        Text = "Hit a bug? Open the diagnostics log and attach it to your report.",
        Font = new Font("Segoe UI", 8.75f),
        ForeColor = SystemColors.GrayText,
        AutoSize = true,
        Anchor = AnchorStyles.None,
        Margin = new Padding(0, 0, 0, 4),
      };
      var btnLog = new Button {
        Text = "Open log folder",
        Font = new Font("Segoe UI", 9f),
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Anchor = AnchorStyles.None,
        Margin = new Padding(0, 0, 0, 12),
        Padding = new Padding(6, 2, 6, 2),
      };
      btnLog.Click += (s, e) => OpenLogFolder();

      var disclaimer = new Label {
        Text = "ACT Discord Triggers is a community plugin — not affiliated with or "
             + "endorsed by Discord, Square Enix, or Advanced Combat Tracker. Provided "
             + "as-is under the MIT License, without warranty; use at your own risk and "
             + "follow Discord's Terms of Service. Originally created by Makar8000.",
        Font = new Font("Segoe UI", 8.25f),
        ForeColor = SystemColors.GrayText,
        AutoSize = true,
        MaximumSize = new Size(540, 0),
        Anchor = AnchorStyles.None,
        Margin = new Padding(0, 6, 0, 0),
      };

      root.Controls.Add(logo);
      root.Controls.Add(title);
      root.Controls.Add(version);
      root.Controls.Add(tagline);
      root.Controls.Add(MakeSeparator());
      root.Controls.Add(links);
      root.Controls.Add(MakeSeparator());
      root.Controls.Add(diagLabel);
      root.Controls.Add(btnLog);
      root.Controls.Add(MakeSeparator());
      root.Controls.Add(disclaimer);

      pagInfo.Controls.Add(root);
      pagInfo.ResumeLayout();
    }

    private LinkLabel MakeLink(string text, string url) {
      var ll = new LinkLabel {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI", 10f),
        Margin = new Padding(0, 2, 0, 2),
      };
      ll.LinkClicked += (s, e) => OpenUrl(url);
      return ll;
    }

    // Full-width hairline between sections.
    private static Panel MakeSeparator() {
      return new Panel {
        Height = 1,
        BackColor = SystemColors.ControlLight,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Margin = new Padding(20, 8, 20, 8),
      };
    }

    private static void OpenUrl(string url) {
      try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
      catch { /* no default browser / blocked — nothing useful to do */ }
    }

    // Open Explorer at the diagnostics log (selected if present) so the user can
    // grab the one file we ask for in bug reports.
    private static void OpenLogFolder() {
      try {
        var path = DiagnosticsLog.UnifiedPath;
        if (string.IsNullOrEmpty(path)) return;
        if (File.Exists(path))
          Process.Start("explorer.exe", "/select,\"" + path + "\"");
        else
          Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(path) + "\"");
      } catch { /* best effort */ }
    }

    // Mascot from the embedded resource. Copied into a standalone Bitmap so the
    // source stream can close (GDI+ keeps a stream alive otherwise). Null if the
    // resource is missing — the caller hides the PictureBox.
    private static Image LoadLogo() {
      try {
        var asm = Assembly.GetExecutingAssembly();
        using (var s = asm.GetManifestResourceStream("ACT_DiscordTriggers.logo.png")) {
          if (s == null) return null;
          using (var tmp = Image.FromStream(s)) return new Bitmap(tmp);
        }
      } catch { return null; }
    }

    #endregion
  }
}
