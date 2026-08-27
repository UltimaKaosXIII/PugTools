namespace PugTools {
  partial class Tools {
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

    #region Windows Form Designer generated code

    /// <summary>
    /// Required method for Designer support - do not modify
    /// the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent() {
      components = new System.ComponentModel.Container();
      toolTip1 = new System.Windows.Forms.ToolTip(components);
      chkAssetsUsePTS = new System.Windows.Forms.CheckBox();
      chkPrevAssetsUsePTS = new System.Windows.Forms.CheckBox();
      btnUnloadAllData = new System.Windows.Forms.Button();
      chkVerbose = new System.Windows.Forms.CheckBox();
      chkBuildCompare = new System.Windows.Forms.CheckBox();
      chkRemoveElements = new System.Windows.Forms.CheckBox();
      btnExtract = new System.Windows.Forms.Button();
      btnSearch = new System.Windows.Forms.Button();
      gbxPath = new System.Windows.Forms.GroupBox();
      lblAssetsPath = new System.Windows.Forms.Label();
      txtAssetsPath = new System.Windows.Forms.TextBox();
      btnAssetsPath = new System.Windows.Forms.Button();
      lblPrevAssetsPath = new System.Windows.Forms.Label();
      txtPrevAssetsPath = new System.Windows.Forms.TextBox();
      btnPrevAssetsPath = new System.Windows.Forms.Button();
      lblExtractPath = new System.Windows.Forms.Label();
      txtExtractPath = new System.Windows.Forms.TextBox();
      btnExtractPath = new System.Windows.Forms.Button();
      chkCrossLinkDom = new System.Windows.Forms.CheckBox();
      chkSmartLinkDom = new System.Windows.Forms.CheckBox();
      gbxFormat = new System.Windows.Forms.GroupBox();
      cbxLanguage = new System.Windows.Forms.ComboBox();
      lblLanguage = new System.Windows.Forms.Label();
      cbxExtractFormat = new System.Windows.Forms.ComboBox();
      lblVersion = new System.Windows.Forms.Label();
      txtVersion = new System.Windows.Forms.TextBox();
      gbxLogs = new System.Windows.Forms.GroupBox();
      listBox1 = new System.Windows.Forms.ListBox();
      listBox2 = new System.Windows.Forms.ListBox();
      gbxSQL = new System.Windows.Forms.GroupBox();
      lblSqlAddress = new System.Windows.Forms.Label();
      txtSqlAddress = new System.Windows.Forms.TextBox();
      lblSqlName = new System.Windows.Forms.Label();
      txtSqlName = new System.Windows.Forms.TextBox();
      lblSqlUsername = new System.Windows.Forms.Label();
      txtSqlUsername = new System.Windows.Forms.TextBox();
      lblSqlPassword = new System.Windows.Forms.Label();
      txtSqlPassword = new System.Windows.Forms.TextBox();
      btnToggleSql = new System.Windows.Forms.Button();
      gbxExtract = new System.Windows.Forms.GroupBox();
      lblExtractDesc = new System.Windows.Forms.Label();
      cbxExtractors = new System.Windows.Forms.ComboBox();
      gbxTools = new System.Windows.Forms.GroupBox();
      btnAssetBrowser = new System.Windows.Forms.Button();
      btnNodeBrowser = new System.Windows.Forms.Button();
      btnModelBrowser = new System.Windows.Forms.Button();
      btnWorldBrowser = new System.Windows.Forms.Button();
      btnCreateSql = new System.Windows.Forms.Button();
      btnFileCompare = new System.Windows.Forms.Button();
      gbxFQN = new System.Windows.Forms.GroupBox();
      tbxFqnSearch = new System.Windows.Forms.TextBox();
      lblFqnDesc = new System.Windows.Forms.Label();
      progressBar1 = new System.Windows.Forms.ProgressBar();
      gbxPath.SuspendLayout();
      gbxFormat.SuspendLayout();
      gbxLogs.SuspendLayout();
      gbxSQL.SuspendLayout();
      gbxExtract.SuspendLayout();
      gbxTools.SuspendLayout();
      gbxFQN.SuspendLayout();
      SuspendLayout();
      //
      // chkAssetsUsePTS
      //
      chkAssetsUsePTS.AutoSize = true;
      chkAssetsUsePTS.Location = new System.Drawing.Point(513, 23);
      chkAssetsUsePTS.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      chkAssetsUsePTS.Name = "chkAssetsUsePTS";
      chkAssetsUsePTS.Size = new System.Drawing.Size(46, 19);
      chkAssetsUsePTS.TabIndex = 2;
      chkAssetsUsePTS.Text = "PTS";
      toolTip1.SetToolTip(chkAssetsUsePTS, "Loads PTS assets if checked.");
      chkAssetsUsePTS.UseVisualStyleBackColor = true;
      chkAssetsUsePTS.CheckedChanged += ChkUsePTSAssets_Changed;
      //
      // chkPrevAssetsUsePTS
      //
      chkPrevAssetsUsePTS.AutoSize = true;
      chkPrevAssetsUsePTS.Location = new System.Drawing.Point(513, 51);
      chkPrevAssetsUsePTS.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      chkPrevAssetsUsePTS.Name = "chkPrevAssetsUsePTS";
      chkPrevAssetsUsePTS.Size = new System.Drawing.Size(46, 19);
      chkPrevAssetsUsePTS.TabIndex = 5;
      chkPrevAssetsUsePTS.Text = "PTS";
      toolTip1.SetToolTip(chkPrevAssetsUsePTS, "Loads PTS assets if checked.");
      chkPrevAssetsUsePTS.UseVisualStyleBackColor = true;
      chkPrevAssetsUsePTS.CheckedChanged += ChkPrevUsePTSAssets_Changed;
      //
      // btnUnloadAllData
      //
      btnUnloadAllData.Location = new System.Drawing.Point(568, 14);
      btnUnloadAllData.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      btnUnloadAllData.Name = "btnUnloadAllData";
      btnUnloadAllData.Size = new System.Drawing.Size(104, 25);
      btnUnloadAllData.TabIndex = 46;
      btnUnloadAllData.Text = "Unload All Data";
      toolTip1.SetToolTip(btnUnloadAllData, "Unload current DOM and assets.");
      btnUnloadAllData.UseVisualStyleBackColor = true;
      btnUnloadAllData.Click += BtnUnloadAllData_Click;
      //
      // chkVerbose
      //
      chkVerbose.AutoSize = true;
      chkVerbose.Checked = true;
      chkVerbose.CheckState = System.Windows.Forms.CheckState.Checked;
      chkVerbose.Location = new System.Drawing.Point(113, 23);
      chkVerbose.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      chkVerbose.Name = "chkVerbose";
      chkVerbose.Size = new System.Drawing.Size(67, 19);
      chkVerbose.TabIndex = 14;
      chkVerbose.Text = "Verbose";
      toolTip1.SetToolTip(chkVerbose, "Export all data.");
      chkVerbose.UseVisualStyleBackColor = true;
      chkVerbose.CheckedChanged += ChkVerbose_Changed;
      //
      // chkBuildCompare
      //
      chkBuildCompare.AutoSize = true;
      chkBuildCompare.Location = new System.Drawing.Point(181, 23);
      chkBuildCompare.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      chkBuildCompare.Name = "chkBuildCompare";
      chkBuildCompare.Size = new System.Drawing.Size(110, 19);
      chkBuildCompare.TabIndex = 18;
      chkBuildCompare.Text = "Compare Builds";
      toolTip1.SetToolTip(chkBuildCompare, "Generate an output of the changes between current and the previous builds.");
      chkBuildCompare.UseVisualStyleBackColor = true;
      chkBuildCompare.CheckedChanged += ChkBuildCompare_Changed;
      //
      // chkRemoveElements
      //
      chkRemoveElements.AutoSize = true;
      chkRemoveElements.Checked = true;
      chkRemoveElements.CheckState = System.Windows.Forms.CheckState.Checked;
      chkRemoveElements.Location = new System.Drawing.Point(293, 23);
      chkRemoveElements.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      chkRemoveElements.Name = "chkRemoveElements";
      chkRemoveElements.Size = new System.Drawing.Size(115, 19);
      chkRemoveElements.TabIndex = 17;
      chkRemoveElements.Text = "Remove Element";
      toolTip1.SetToolTip(chkRemoveElements, "Remove unchanged elements.");
      chkRemoveElements.UseVisualStyleBackColor = true;
      chkRemoveElements.CheckedChanged += ChkRemoveElements_Changed;
      //
      // btnExtract
      //
      btnExtract.Location = new System.Drawing.Point(187, 52);
      btnExtract.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      btnExtract.Name = "btnExtract";
      btnExtract.Size = new System.Drawing.Size(79, 27);
      btnExtract.TabIndex = 19;
      btnExtract.Text = "Extract";
      toolTip1.SetToolTip(btnExtract, "Opens the extraction dialog to extract data and compare builds.");
      btnExtract.UseVisualStyleBackColor = true;
      btnExtract.Click += BtnExtract_Click;
      //
      // btnSearch
      //
      btnSearch.Location = new System.Drawing.Point(204, 18);
      btnSearch.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      btnSearch.Name = "btnSearch";
      btnSearch.Size = new System.Drawing.Size(62, 25);
      btnSearch.TabIndex = 23;
      btnSearch.Text = "Search";
      toolTip1.SetToolTip(btnSearch, "Perform search");
      btnSearch.UseVisualStyleBackColor = true;
      btnSearch.Click += BtnSearch_Click;
      //
      // gbxPath
      //
      gbxPath.Controls.Add(lblAssetsPath);
      gbxPath.Controls.Add(txtAssetsPath);
      gbxPath.Controls.Add(btnAssetsPath);
      gbxPath.Controls.Add(chkAssetsUsePTS);
      gbxPath.Controls.Add(lblPrevAssetsPath);
      gbxPath.Controls.Add(txtPrevAssetsPath);
      gbxPath.Controls.Add(btnPrevAssetsPath);
      gbxPath.Controls.Add(chkPrevAssetsUsePTS);
      gbxPath.Controls.Add(lblExtractPath);
      gbxPath.Controls.Add(txtExtractPath);
      gbxPath.Controls.Add(btnExtractPath);
      gbxPath.Controls.Add(chkCrossLinkDom);
      gbxPath.Controls.Add(chkSmartLinkDom);
      gbxPath.Controls.Add(btnUnloadAllData);
      gbxPath.Location = new System.Drawing.Point(14, 7);
      gbxPath.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      gbxPath.Name = "gbxPath";
      gbxPath.Padding = new System.Windows.Forms.Padding(4, 3, 4, 3);
      gbxPath.Size = new System.Drawing.Size(681, 105);
      gbxPath.TabIndex = 46;
      gbxPath.TabStop = false;
      gbxPath.Text = "Path Information";
      //
      // lblAssetsPath
      //
      lblAssetsPath.AutoSize = true;
      lblAssetsPath.Location = new System.Drawing.Point(7, 18);
      lblAssetsPath.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
      lblAssetsPath.Name = "lblAssetsPath";
      lblAssetsPath.Size = new System.Drawing.Size(71, 15);
      lblAssetsPath.TabIndex = 3;
      lblAssetsPath.Text = "Game Folder";
      //
      // txtAssetsPath
      //
      txtAssetsPath.Location = new System.Drawing.Point(110, 15);
      txtAssetsPath.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      txtAssetsPath.Name = "txtAssetsPath";
      txtAssetsPath.Size = new System.Drawing.Size(314, 23);
      txtAssetsPath.TabIndex = 0;
      txtAssetsPath.TextChanged += TxtAssetsPath_Changed;
      //
      // btnAssetsPath
      //
      btnAssetsPath.Image = Properties.Resources.ShieldRed;
      btnAssetsPath.Location = new System.Drawing.Point(432, 14);
      btnAssetsPath.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      btnAssetsPath.Name = "btnAssetsPath";
      btnAssetsPath.Size = new System.Drawing.Size(74, 25);
      btnAssetsPath.TabIndex = 1;
      btnAssetsPath.Text = "Select";
      btnAssetsPath.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageBeforeText;
      btnAssetsPath.UseVisualStyleBackColor = true;
      btnAssetsPath.Click += BtnAssetsPath_Click;
      //
      // lblPrevAssetsPath
      //
      lblPrevAssetsPath.AutoSize = true;
      lblPrevAssetsPath.Location = new System.Drawing.Point(7, 47);
      lblPrevAssetsPath.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
      lblPrevAssetsPath.Name = "lblPrevAssetsPath";
      lblPrevAssetsPath.Size = new System.Drawing.Size(97, 15);
      lblPrevAssetsPath.TabIndex = 43;
      lblPrevAssetsPath.Text = "Prev Game";
      //
      // txtPrevAssetsPath
      //
      txtPrevAssetsPath.Location = new System.Drawing.Point(110, 44);
      txtPrevAssetsPath.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      txtPrevAssetsPath.Name = "txtPrevAssetsPath";
      txtPrevAssetsPath.Size = new System.Drawing.Size(314, 23);
      txtPrevAssetsPath.TabIndex = 3;
      txtPrevAssetsPath.TextChanged += TxtPrevAssetsPath_Changed;
      //
      // btnPrevAssetsPath
      //
      btnPrevAssetsPath.Image = Properties.Resources.ShieldGreen;
      btnPrevAssetsPath.Location = new System.Drawing.Point(432, 43);
      btnPrevAssetsPath.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      btnPrevAssetsPath.Name = "btnPrevAssetsPath";
      btnPrevAssetsPath.Size = new System.Drawing.Size(74, 25);
      btnPrevAssetsPath.TabIndex = 4;
      btnPrevAssetsPath.Text = "Select";
      btnPrevAssetsPath.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageBeforeText;
      btnPrevAssetsPath.UseVisualStyleBackColor = true;
      btnPrevAssetsPath.Click += BtnPrevAssetsPath_Click;
      //
      // lblExtractPath
      //
      lblExtractPath.AutoSize = true;
      lblExtractPath.Location = new System.Drawing.Point(7, 76);
      lblExtractPath.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
      lblExtractPath.Name = "lblExtractPath";
      lblExtractPath.Size = new System.Drawing.Size(78, 15);
      lblExtractPath.TabIndex = 35;
      lblExtractPath.Text = "Extract Folder";
      //
      // txtExtractPath
      //
      txtExtractPath.Location = new System.Drawing.Point(110, 73);
      txtExtractPath.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      txtExtractPath.Name = "txtExtractPath";
      txtExtractPath.Size = new System.Drawing.Size(314, 23);
      txtExtractPath.TabIndex = 6;
      txtExtractPath.TextChanged += TxtExtractPath_Changed;
      //
      // btnExtractPath
      //
      btnExtractPath.Location = new System.Drawing.Point(432, 72);
      btnExtractPath.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      btnExtractPath.Name = "btnExtractPath";
      btnExtractPath.Size = new System.Drawing.Size(74, 25);
      btnExtractPath.TabIndex = 7;
      btnExtractPath.Text = "Select";
      btnExtractPath.UseVisualStyleBackColor = true;
      btnExtractPath.Click += BtnExtractPath_Click;
      //
      // chkCrossLinkDom
      //
      chkCrossLinkDom.AutoSize = true;
      chkCrossLinkDom.Location = new System.Drawing.Point(513, 80);
      chkCrossLinkDom.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      chkCrossLinkDom.Name = "chkCrossLinkDom";
      chkCrossLinkDom.Size = new System.Drawing.Size(57, 19);
      chkCrossLinkDom.TabIndex = 6;
      chkCrossLinkDom.Text = "X-Lnk";
      chkCrossLinkDom.UseVisualStyleBackColor = true;
      chkCrossLinkDom.CheckedChanged += ChkCrossLinkDom_Changed;
      //
      // chkSmartLinkDom
      //
      chkSmartLinkDom.AutoSize = true;
      chkSmartLinkDom.Location = new System.Drawing.Point(573, 80);
      chkSmartLinkDom.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      chkSmartLinkDom.Name = "chkSmartLinkDom";
      chkSmartLinkDom.Size = new System.Drawing.Size(81, 19);
      chkSmartLinkDom.TabIndex = 47;
      chkSmartLinkDom.Text = "Smart-Lnk";
      chkSmartLinkDom.UseVisualStyleBackColor = true;
      //
      // gbxFormat
      //
      gbxFormat.Controls.Add(cbxLanguage);
      gbxFormat.Controls.Add(lblLanguage);
      gbxFormat.Controls.Add(cbxExtractFormat);
      gbxFormat.Controls.Add(chkVerbose);
      gbxFormat.Controls.Add(chkBuildCompare);
      gbxFormat.Controls.Add(chkRemoveElements);
      gbxFormat.Controls.Add(lblVersion);
      gbxFormat.Controls.Add(txtVersion);
      gbxFormat.Location = new System.Drawing.Point(14, 118);
      gbxFormat.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      gbxFormat.Name = "gbxFormat";
      gbxFormat.Padding = new System.Windows.Forms.Padding(4, 3, 4, 3);
      gbxFormat.Size = new System.Drawing.Size(681, 47);
      gbxFormat.TabIndex = 24;
      gbxFormat.TabStop = false;
      gbxFormat.Text = "Extract Format";
      //
      // cbxLanguage
      //
      cbxLanguage.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
      cbxLanguage.FormattingEnabled = true;
      cbxLanguage.Items.AddRange(new object[] { "en-us", "de-de", "fr-fr" });
      cbxLanguage.Location = new System.Drawing.Point(472, 16);
      cbxLanguage.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      cbxLanguage.Name = "cbxLanguage";
      cbxLanguage.Size = new System.Drawing.Size(69, 23);
      cbxLanguage.TabIndex = 16;
      cbxLanguage.SelectedIndexChanged += CbxLanguage_Changed;
      //
      // lblLanguage
      //
      lblLanguage.AutoSize = true;
      lblLanguage.Location = new System.Drawing.Point(408, 18);
      lblLanguage.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
      lblLanguage.Name = "lblLanguage";
      lblLanguage.Size = new System.Drawing.Size(62, 15);
      lblLanguage.TabIndex = 17;
      lblLanguage.Text = "Language:";
      //
      // cbxExtractFormat
      //
      cbxExtractFormat.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
      cbxExtractFormat.FormattingEnabled = true;
      cbxExtractFormat.Items.AddRange(new object[] { "JSON", "SQL", "TXT", "XML" });
      cbxExtractFormat.Location = new System.Drawing.Point(7, 16);
      cbxExtractFormat.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      cbxExtractFormat.Name = "cbxExtractFormat";
      cbxExtractFormat.Size = new System.Drawing.Size(100, 23);
      cbxExtractFormat.TabIndex = 15;
      cbxExtractFormat.SelectedIndexChanged += CbxExtractFormat_Changed;
      //
      // lblVersion
      //
      lblVersion.AutoSize = true;
      lblVersion.Location = new System.Drawing.Point(546, 18);
      lblVersion.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
      lblVersion.Name = "lblVersion";
      lblVersion.Size = new System.Drawing.Size(45, 15);
      lblVersion.TabIndex = 13;
      lblVersion.Text = "Version";
      //
      // txtVersion
      //
      txtVersion.Location = new System.Drawing.Point(600, 15);
      txtVersion.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      txtVersion.Name = "txtVersion";
      txtVersion.Size = new System.Drawing.Size(72, 23);
      txtVersion.TabIndex = 12;
      txtVersion.TextChanged += TxtVersion_Changed;
      //
      // gbxLogs
      //
      gbxLogs.Controls.Add(listBox1);
      gbxLogs.Controls.Add(listBox2);
      gbxLogs.Location = new System.Drawing.Point(14, 171);
      gbxLogs.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      gbxLogs.Name = "gbxLogs";
      gbxLogs.Padding = new System.Windows.Forms.Padding(4, 3, 4, 3);
      gbxLogs.Size = new System.Drawing.Size(398, 470);
      gbxLogs.TabIndex = 47;
      gbxLogs.TabStop = false;
      gbxLogs.Text = "Logs";
      //
      // listBox1
      //
      listBox1.FormattingEnabled = true;
      listBox1.ItemHeight = 15;
      listBox1.Location = new System.Drawing.Point(12, 21);
      listBox1.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      listBox1.Name = "listBox1";
      listBox1.ScrollAlwaysVisible = true;
      listBox1.Size = new System.Drawing.Size(375, 184);
      listBox1.TabIndex = 0;
      listBox1.TabStop = false;
      //
      // listBox2
      //
      listBox2.FormattingEnabled = true;
      listBox2.ItemHeight = 15;
      listBox2.Location = new System.Drawing.Point(12, 212);
      listBox2.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      listBox2.Name = "listBox2";
      listBox2.Size = new System.Drawing.Size(375, 244);
      listBox2.TabIndex = 0;
      listBox2.TabStop = false;
      //
      // gbxSQL
      //
      gbxSQL.Controls.Add(lblSqlAddress);
      gbxSQL.Controls.Add(txtSqlAddress);
      gbxSQL.Controls.Add(lblSqlName);
      gbxSQL.Controls.Add(txtSqlName);
      gbxSQL.Controls.Add(lblSqlUsername);
      gbxSQL.Controls.Add(txtSqlUsername);
      gbxSQL.Controls.Add(lblSqlPassword);
      gbxSQL.Controls.Add(txtSqlPassword);
      gbxSQL.Controls.Add(btnToggleSql);
      gbxSQL.Location = new System.Drawing.Point(418, 171);
      gbxSQL.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      gbxSQL.Name = "gbxSQL";
      gbxSQL.Padding = new System.Windows.Forms.Padding(4, 3, 4, 3);
      gbxSQL.Size = new System.Drawing.Size(278, 145);
      gbxSQL.TabIndex = 26;
      gbxSQL.TabStop = false;
      gbxSQL.Text = "Database Options";
      //
      // lblSqlAddress
      //
      lblSqlAddress.AutoSize = true;
      lblSqlAddress.Location = new System.Drawing.Point(9, 18);
      lblSqlAddress.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
      lblSqlAddress.Name = "lblSqlAddress";
      lblSqlAddress.Size = new System.Drawing.Size(80, 15);
      lblSqlAddress.TabIndex = 10;
      lblSqlAddress.Text = "DB IP Address";
      //
      // txtSqlAddress
      //
      txtSqlAddress.Location = new System.Drawing.Point(9, 37);
      txtSqlAddress.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      txtSqlAddress.Name = "txtSqlAddress";
      txtSqlAddress.Size = new System.Drawing.Size(125, 23);
      txtSqlAddress.TabIndex = 5;
      txtSqlAddress.Text = "127.0.0.1";
      //
      // lblSqlName
      //
      lblSqlName.AutoSize = true;
      lblSqlName.Location = new System.Drawing.Point(142, 18);
      lblSqlName.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
      lblSqlName.Name = "lblSqlName";
      lblSqlName.Size = new System.Drawing.Size(57, 15);
      lblSqlName.TabIndex = 11;
      lblSqlName.Text = "DB Name";
      //
      // txtSqlName
      //
      txtSqlName.Location = new System.Drawing.Point(142, 37);
      txtSqlName.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      txtSqlName.Name = "txtSqlName";
      txtSqlName.Size = new System.Drawing.Size(125, 23);
      txtSqlName.TabIndex = 8;
      txtSqlName.Text = "tor_dump";
      //
      // lblSqlUsername
      //
      lblSqlUsername.AutoSize = true;
      lblSqlUsername.Location = new System.Drawing.Point(9, 65);
      lblSqlUsername.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
      lblSqlUsername.Name = "lblSqlUsername";
      lblSqlUsername.Size = new System.Drawing.Size(78, 15);
      lblSqlUsername.TabIndex = 6;
      lblSqlUsername.Text = "DB Username";
      //
      // txtSqlUsername
      //
      txtSqlUsername.Location = new System.Drawing.Point(9, 83);
      txtSqlUsername.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      txtSqlUsername.Name = "txtSqlUsername";
      txtSqlUsername.Size = new System.Drawing.Size(125, 23);
      txtSqlUsername.TabIndex = 6;
      txtSqlUsername.Text = "root";
      //
      // lblSqlPassword
      //
      lblSqlPassword.AutoSize = true;
      lblSqlPassword.Location = new System.Drawing.Point(142, 65);
      lblSqlPassword.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
      lblSqlPassword.Name = "lblSqlPassword";
      lblSqlPassword.Size = new System.Drawing.Size(75, 15);
      lblSqlPassword.TabIndex = 7;
      lblSqlPassword.Text = "DB Password";
      //
      // txtSqlPassword
      //
      txtSqlPassword.Location = new System.Drawing.Point(142, 83);
      txtSqlPassword.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      txtSqlPassword.Name = "txtSqlPassword";
      txtSqlPassword.PasswordChar = '*';
      txtSqlPassword.Size = new System.Drawing.Size(125, 23);
      txtSqlPassword.TabIndex = 7;
      //
      // btnToggleSql
      //
      btnToggleSql.Location = new System.Drawing.Point(142, 112);
      btnToggleSql.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      btnToggleSql.Name = "btnToggleSql";
      btnToggleSql.Size = new System.Drawing.Size(126, 25);
      btnToggleSql.TabIndex = 9;
      btnToggleSql.Text = "Mysql Off";
      btnToggleSql.UseVisualStyleBackColor = true;
      btnToggleSql.Click += BtnToggleSql_Click;
      //
      // gbxExtract
      //
      gbxExtract.Controls.Add(lblExtractDesc);
      gbxExtract.Controls.Add(cbxExtractors);
      gbxExtract.Controls.Add(btnExtract);
      gbxExtract.Location = new System.Drawing.Point(418, 321);
      gbxExtract.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      gbxExtract.Name = "gbxExtract";
      gbxExtract.Padding = new System.Windows.Forms.Padding(4, 3, 4, 3);
      gbxExtract.Size = new System.Drawing.Size(278, 90);
      gbxExtract.TabIndex = 31;
      gbxExtract.TabStop = false;
      gbxExtract.Text = "Extractors";
      //
      // lblExtractDesc
      //
      lblExtractDesc.AutoSize = true;
      lblExtractDesc.Cursor = System.Windows.Forms.Cursors.IBeam;
      lblExtractDesc.Location = new System.Drawing.Point(12, 16);
      lblExtractDesc.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
      lblExtractDesc.MaximumSize = new System.Drawing.Size(268, 0);
      lblExtractDesc.Name = "lblExtractDesc";
      lblExtractDesc.Size = new System.Drawing.Size(258, 30);
      lblExtractDesc.TabIndex = 29;
      lblExtractDesc.Text = "Click and select what you want from the dialog window. The default is to dump everything.";
      //
      // cbxExtractors
      //
      cbxExtractors.BackColor = System.Drawing.SystemColors.ControlLightLight;
      cbxExtractors.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
      cbxExtractors.FormattingEnabled = true;
      cbxExtractors.Location = new System.Drawing.Point(12, 54);
      cbxExtractors.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      cbxExtractors.Name = "cbxExtractors";
      cbxExtractors.Size = new System.Drawing.Size(167, 23);
      cbxExtractors.Sorted = true;
      cbxExtractors.TabIndex = 20;
      //
      // gbxTools
      //
      gbxTools.Controls.Add(btnAssetBrowser);
      gbxTools.Controls.Add(btnNodeBrowser);
      gbxTools.Controls.Add(btnModelBrowser);
      gbxTools.Controls.Add(btnWorldBrowser);
      gbxTools.Controls.Add(btnCreateSql);
      gbxTools.Controls.Add(btnFileCompare);
      gbxTools.Location = new System.Drawing.Point(419, 418);
      gbxTools.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      gbxTools.Name = "gbxTools";
      gbxTools.Padding = new System.Windows.Forms.Padding(4, 3, 4, 3);
      gbxTools.Size = new System.Drawing.Size(278, 125);
      gbxTools.TabIndex = 34;
      gbxTools.TabStop = false;
      gbxTools.Text = "Tools";
      //
      // btnAssetBrowser
      //
      btnAssetBrowser.Location = new System.Drawing.Point(7, 22);
      btnAssetBrowser.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      btnAssetBrowser.Name = "btnAssetBrowser";
      btnAssetBrowser.Size = new System.Drawing.Size(126, 27);
      btnAssetBrowser.TabIndex = 24;
      btnAssetBrowser.Text = "Asset Browser";
      btnAssetBrowser.UseVisualStyleBackColor = true;
      btnAssetBrowser.Click += BtnAssetBrowser_Click;
      //
      // btnNodeBrowser
      //
      btnNodeBrowser.Location = new System.Drawing.Point(139, 22);
      btnNodeBrowser.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      btnNodeBrowser.Name = "btnNodeBrowser";
      btnNodeBrowser.Size = new System.Drawing.Size(126, 27);
      btnNodeBrowser.TabIndex = 50;
      btnNodeBrowser.Text = "Node Browser";
      btnNodeBrowser.UseVisualStyleBackColor = true;
      btnNodeBrowser.Click += BtnNodeBrowser_Click;
      //
      // btnModelBrowser
      //
      btnModelBrowser.Location = new System.Drawing.Point(7, 55);
      btnModelBrowser.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      btnModelBrowser.Name = "btnModelBrowser";
      btnModelBrowser.Size = new System.Drawing.Size(126, 27);
      btnModelBrowser.TabIndex = 48;
      btnModelBrowser.Text = "Model Browser";
      btnModelBrowser.UseVisualStyleBackColor = true;
      btnModelBrowser.Click += BtnModelBrowser_Click;
      //
      // btnWorldBrowser
      //
      btnWorldBrowser.Location = new System.Drawing.Point(139, 55);
      btnWorldBrowser.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      btnWorldBrowser.Name = "btnWorldBrowser";
      btnWorldBrowser.Size = new System.Drawing.Size(126, 27);
      btnWorldBrowser.TabIndex = 49;
      btnWorldBrowser.Text = "World Browser";
      btnWorldBrowser.UseVisualStyleBackColor = true;
      btnWorldBrowser.Click += BtnWorldBrowser_Click;
      //
      // btnCreateSql
      //
      btnCreateSql.Location = new System.Drawing.Point(7, 89);
      btnCreateSql.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      btnCreateSql.Name = "btnCreateSql";
      btnCreateSql.Size = new System.Drawing.Size(126, 27);
      btnCreateSql.TabIndex = 1;
      btnCreateSql.Text = "Create SQL";
      btnCreateSql.UseVisualStyleBackColor = true;
      btnCreateSql.Click += BtnCreateSql_Click;
      //
      // btnFileCompare
      //
      btnFileCompare.Location = new System.Drawing.Point(139, 89);
      btnFileCompare.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      btnFileCompare.Name = "btnFileCompare";
      btnFileCompare.Size = new System.Drawing.Size(126, 27);
      btnFileCompare.TabIndex = 51;
      btnFileCompare.Text = "File Compare";
      btnFileCompare.UseVisualStyleBackColor = true;
      btnFileCompare.Click += BtnFileCompare_Click;
      //
      // gbxFQN
      //
      gbxFQN.Controls.Add(tbxFqnSearch);
      gbxFQN.Controls.Add(btnSearch);
      gbxFQN.Controls.Add(lblFqnDesc);
      gbxFQN.Location = new System.Drawing.Point(419, 549);
      gbxFQN.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      gbxFQN.Name = "gbxFQN";
      gbxFQN.Padding = new System.Windows.Forms.Padding(4, 3, 4, 3);
      gbxFQN.Size = new System.Drawing.Size(278, 91);
      gbxFQN.TabIndex = 33;
      gbxFQN.TabStop = false;
      gbxFQN.Text = "FQN Search";
      //
      // tbxFqnSearch
      //
      tbxFqnSearch.Location = new System.Drawing.Point(10, 20);
      tbxFqnSearch.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      tbxFqnSearch.Name = "tbxFqnSearch";
      tbxFqnSearch.Size = new System.Drawing.Size(188, 23);
      tbxFqnSearch.TabIndex = 22;
      //
      // lblFqnDesc
      //
      lblFqnDesc.AutoSize = true;
      lblFqnDesc.Cursor = System.Windows.Forms.Cursors.IBeam;
      lblFqnDesc.Location = new System.Drawing.Point(7, 50);
      lblFqnDesc.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
      lblFqnDesc.MaximumSize = new System.Drawing.Size(262, 0);
      lblFqnDesc.Name = "lblFqnDesc";
      lblFqnDesc.Size = new System.Drawing.Size(258, 30);
      lblFqnDesc.TabIndex = 26;
      lblFqnDesc.Text = "Be careful what you put in here. As it will search and output all occurences in the GOM.";
      lblFqnDesc.TextAlign = System.Drawing.ContentAlignment.TopCenter;
      //
      // progressBar1
      //
      progressBar1.Location = new System.Drawing.Point(14, 655);
      progressBar1.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      progressBar1.MarqueeAnimationSpeed = 1000;
      progressBar1.Name = "progressBar1";
      progressBar1.Size = new System.Drawing.Size(681, 25);
      progressBar1.TabIndex = 45;
      //
      // Tools
      //
      AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
      AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
      ClientSize = new System.Drawing.Size(709, 695);
      Controls.Add(gbxPath);
      Controls.Add(gbxFormat);
      Controls.Add(gbxLogs);
      Controls.Add(gbxSQL);
      Controls.Add(gbxExtract);
      Controls.Add(gbxTools);
      Controls.Add(gbxFQN);
      Controls.Add(progressBar1);
      FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
      Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      MaximizeBox = false;
      MinimizeBox = false;
      Name = "Tools";
      StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
      Text = "SWTOR Pug Tools";
      gbxPath.ResumeLayout(false);
      gbxPath.PerformLayout();
      gbxFormat.ResumeLayout(false);
      gbxFormat.PerformLayout();
      gbxLogs.ResumeLayout(false);
      gbxSQL.ResumeLayout(false);
      gbxSQL.PerformLayout();
      gbxExtract.ResumeLayout(false);
      gbxExtract.PerformLayout();
      gbxTools.ResumeLayout(false);
      gbxFQN.ResumeLayout(false);
      gbxFQN.PerformLayout();
      ResumeLayout(false);
    }

    #endregion

    private System.Windows.Forms.Button btnAssetBrowser;
    private System.Windows.Forms.Button btnAssetsPath;
    private System.Windows.Forms.Button btnCreateSql;
    private System.Windows.Forms.Button btnExtract;
    private System.Windows.Forms.Button btnExtractPath;
    private System.Windows.Forms.Button btnFileCompare;
    private System.Windows.Forms.Button btnModelBrowser;
    private System.Windows.Forms.Button btnNodeBrowser;
    private System.Windows.Forms.Button btnPrevAssetsPath;
    private System.Windows.Forms.Button btnSearch;
    private System.Windows.Forms.Button btnToggleSql;
    private System.Windows.Forms.Button btnUnloadAllData;
    private System.Windows.Forms.Button btnWorldBrowser;
    private System.Windows.Forms.CheckBox chkBuildCompare;
    private System.Windows.Forms.CheckBox chkCrossLinkDom;
    private System.Windows.Forms.ComboBox cbxExtractFormat;
    private System.Windows.Forms.ComboBox cbxLanguage;
    private System.Windows.Forms.Label lblLanguage;
    private System.Windows.Forms.CheckBox chkPrevAssetsUsePTS;
    private System.Windows.Forms.CheckBox chkRemoveElements;
    private System.Windows.Forms.CheckBox chkSmartLinkDom;
    private System.Windows.Forms.CheckBox chkAssetsUsePTS;
    private System.Windows.Forms.CheckBox chkVerbose;
    private System.Windows.Forms.ComboBox cbxExtractors;
    private System.Windows.Forms.GroupBox gbxExtract;
    private System.Windows.Forms.GroupBox gbxFormat;
    private System.Windows.Forms.GroupBox gbxFQN;
    private System.Windows.Forms.GroupBox gbxLogs;
    private System.Windows.Forms.GroupBox gbxPath;
    private System.Windows.Forms.GroupBox gbxSQL;
    private System.Windows.Forms.GroupBox gbxTools;
    private System.Windows.Forms.Label lblAssetsPath;
    private System.Windows.Forms.Label lblExtractDesc;
    private System.Windows.Forms.Label lblExtractPath;
    private System.Windows.Forms.Label lblFqnDesc;
    private System.Windows.Forms.Label lblPrevAssetsPath;
    private System.Windows.Forms.Label lblSqlAddress;
    private System.Windows.Forms.Label lblSqlName;
    private System.Windows.Forms.Label lblSqlPassword;
    private System.Windows.Forms.Label lblSqlUsername;
    private System.Windows.Forms.Label lblVersion;
    private System.Windows.Forms.ListBox listBox1;
    private System.Windows.Forms.ListBox listBox2;
    private System.Windows.Forms.ProgressBar progressBar1;
    private System.Windows.Forms.TextBox txtAssetsPath;
    private System.Windows.Forms.TextBox txtExtractPath;
    private System.Windows.Forms.TextBox tbxFqnSearch;
    private System.Windows.Forms.TextBox txtPrevAssetsPath;
    private System.Windows.Forms.TextBox txtSqlAddress;
    private System.Windows.Forms.TextBox txtSqlName;
    private System.Windows.Forms.TextBox txtSqlPassword;
    private System.Windows.Forms.TextBox txtSqlUsername;
    private System.Windows.Forms.TextBox txtVersion;
    private System.Windows.Forms.ToolTip toolTip1;
  }
}
