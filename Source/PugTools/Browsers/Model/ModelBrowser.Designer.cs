namespace PugTools {
  partial class ModelBrowser {
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
      this.components = new System.ComponentModel.Container();
      System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(ModelBrowser));
      this.splitContainer1 = new System.Windows.Forms.SplitContainer();
      this.splitContainer2 = new System.Windows.Forms.SplitContainer();
      this.splitContainer3 = new System.Windows.Forms.SplitContainer();
      this.splitContainer4 = new System.Windows.Forms.SplitContainer();
      //
      this.btnStopRender = new System.Windows.Forms.Button();
      this.btnToggleData = new System.Windows.Forms.Button();
      this.btnExport = new System.Windows.Forms.Button();
      this.btnHelp = new System.Windows.Forms.Button();
      this.treeViewFast1 = new TreeViewFast.Controls.TreeViewFast();
      this.imageList1 = new System.Windows.Forms.ImageList(this.components);
      this.contextMenuStrip1 = new System.Windows.Forms.ContextMenuStrip(this.components);
      this.contextMenuStrip2 = new System.Windows.Forms.ContextMenuStrip(this.components);
      this.toolStripMenuItem1 = new System.Windows.Forms.ToolStripMenuItem();
      //
      this.renderPanel = new System.Windows.Forms.Panel();
      this.loadingSwirl1 = new System.Windows.Forms.PictureBox();
      //
      this.treeViewFast2 = new TreeViewFast.Controls.TreeViewFast();
      this.dataGridView1 = new System.Windows.Forms.DataGridView();
      this.contextMenuStrip3 = new System.Windows.Forms.ContextMenuStrip(this.components);
      this.toolStripMenuItem2 = new System.Windows.Forms.ToolStripMenuItem();
      this.contextMenuStrip4 = new System.Windows.Forms.ContextMenuStrip(this.components);
      this.toolStripMenuItem3 = new System.Windows.Forms.ToolStripMenuItem();
      //
      this.statusStrip1 = new System.Windows.Forms.StatusStrip();
      this.toolStripStatusLabel1 = new System.Windows.Forms.ToolStripStatusLabel();
      this.toolStripProgressBar1 = new System.Windows.Forms.ToolStripProgressBar();
      //
      this.backgroundWorker1 = new System.ComponentModel.BackgroundWorker();
      this.backgroundWorker2 = new System.ComponentModel.BackgroundWorker();
      this.backgroundWorker3 = new System.ComponentModel.BackgroundWorker();
      //
      ((System.ComponentModel.ISupportInitialize)(this.splitContainer1)).BeginInit();
      this.splitContainer1.Panel1.SuspendLayout();
      this.splitContainer1.Panel2.SuspendLayout();
      this.splitContainer1.SuspendLayout();
      ((System.ComponentModel.ISupportInitialize)(this.splitContainer2)).BeginInit();
      this.splitContainer2.Panel1.SuspendLayout();
      this.splitContainer2.Panel2.SuspendLayout();
      this.splitContainer2.SuspendLayout();
      ((System.ComponentModel.ISupportInitialize)(this.splitContainer3)).BeginInit();
      this.splitContainer3.Panel1.SuspendLayout();
      this.splitContainer3.Panel2.SuspendLayout();
      this.splitContainer3.SuspendLayout();
      ((System.ComponentModel.ISupportInitialize)(this.splitContainer4)).BeginInit();
      this.splitContainer4.Panel1.SuspendLayout();
      this.splitContainer4.Panel2.SuspendLayout();
      this.splitContainer4.SuspendLayout();
      //
      ((System.ComponentModel.ISupportInitialize)(this.dataGridView1)).BeginInit();
      ((System.ComponentModel.ISupportInitialize)(this.loadingSwirl1)).BeginInit();
      this.contextMenuStrip1.SuspendLayout();
      this.contextMenuStrip2.SuspendLayout();
      this.contextMenuStrip3.SuspendLayout();
      this.contextMenuStrip4.SuspendLayout();
      this.statusStrip1.SuspendLayout();
      this.SuspendLayout();

      /////////////////////////////////////////////////////////////////////////////////////////////
      // SPLIT CONTAINERS
      //

      //
      // splitContainer1
      //
      this.splitContainer1.BorderStyle = System.Windows.Forms.BorderStyle.None;
      this.splitContainer1.Dock = System.Windows.Forms.DockStyle.Fill;
      this.splitContainer1.FixedPanel = System.Windows.Forms.FixedPanel.Panel2;
      this.splitContainer1.IsSplitterFixed = true;
      this.splitContainer1.Location = new System.Drawing.Point(0, 0);
      this.splitContainer1.Margin = new System.Windows.Forms.Padding(0, 0, 0, 0);
      this.splitContainer1.Name = "splitContainer1";
      this.splitContainer1.Orientation = System.Windows.Forms.Orientation.Horizontal;
      this.splitContainer1.Size = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea.Size;
      this.splitContainer1.SplitterDistance = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea.Height - 22;
      this.splitContainer1.SplitterWidth = 1;
      this.splitContainer1.TabIndex = 0;
      this.splitContainer1.TabStop = false;
      //
      // splitContainer1.Panel1
      //
      this.splitContainer1.Panel1.Controls.Add(this.splitContainer2);
      //
      // splitContainer1.Panel2
      //
      this.splitContainer1.Panel2.Controls.Add(this.statusStrip1);
      //
      // splitContainer2
      //
      this.splitContainer2.BorderStyle = System.Windows.Forms.BorderStyle.None;
      this.splitContainer2.Dock = System.Windows.Forms.DockStyle.Fill;
      this.splitContainer2.FixedPanel = System.Windows.Forms.FixedPanel.Panel1;
      this.splitContainer2.Location = new System.Drawing.Point(0, 0);
      this.splitContainer2.Margin = new System.Windows.Forms.Padding(0, 0, 0, 0);
      this.splitContainer2.Name = "splitContainer2";
      this.splitContainer2.Size = new System.Drawing.Size(this.splitContainer1.Width, this.splitContainer1.Panel1.Height);
      this.splitContainer2.SplitterDistance = 350;
      this.splitContainer2.SplitterWidth = 1;
      this.splitContainer2.TabIndex = 0;
      this.splitContainer2.TabStop = false;
      //
      // splitContainer2.Panel1
      //
      this.splitContainer2.Panel1.Controls.Add(this.btnStopRender);
      this.splitContainer2.Panel1.Controls.Add(this.btnToggleData);
      this.splitContainer2.Panel1.Controls.Add(this.btnExport);
      this.splitContainer2.Panel1.Controls.Add(this.btnHelp);
      this.splitContainer2.Panel1.Controls.Add(this.treeViewFast1);
      //
      // splitContainer2.Panel2
      //
      this.splitContainer2.Panel2.Controls.Add(this.splitContainer3);
      //
      // splitContainer3
      //
      this.splitContainer3.BorderStyle = System.Windows.Forms.BorderStyle.None;
      this.splitContainer3.Dock = System.Windows.Forms.DockStyle.Fill;
      this.splitContainer3.FixedPanel = System.Windows.Forms.FixedPanel.Panel2;
      this.splitContainer3.Location = new System.Drawing.Point(0, 0);
      this.splitContainer3.Margin = new System.Windows.Forms.Padding(0, 0, 0, 0);
      this.splitContainer3.Name = "splitContainer3";
      this.splitContainer3.Size = new System.Drawing.Size(this.splitContainer2.Panel2.Width - 350, this.splitContainer2.Height);
      this.splitContainer3.SplitterDistance = this.splitContainer3.Width - 350;
      this.splitContainer3.SplitterWidth = 1;
      this.splitContainer3.TabIndex = 0;
      this.splitContainer3.TabStop = false;
      //
      // splitContainer3.Panel1
      //
      this.splitContainer3.Panel1.AutoScroll = true;
      this.splitContainer3.Panel1.Controls.Add(this.loadingSwirl1);
      this.splitContainer3.Panel1.Controls.Add(this.renderPanel);
      //
      // splitContainer3.Panel2
      //
      this.splitContainer3.Panel2.Controls.Add(this.splitContainer4);
      //
      // splitContainer4
      //
      this.splitContainer4.BorderStyle = System.Windows.Forms.BorderStyle.None;
      this.splitContainer4.Dock = System.Windows.Forms.DockStyle.Fill;
      this.splitContainer4.FixedPanel = System.Windows.Forms.FixedPanel.Panel2;
      this.splitContainer4.Location = new System.Drawing.Point(0, 0);
      this.splitContainer4.Margin = new System.Windows.Forms.Padding(0, 0, 0, 0);
      this.splitContainer4.Name = "splitContainer4";
      this.splitContainer4.Orientation = System.Windows.Forms.Orientation.Horizontal;
      this.splitContainer4.Size = new System.Drawing.Size(350, this.splitContainer3.Height);
      this.splitContainer4.SplitterDistance = splitContainer4.Height - 445;
      this.splitContainer4.SplitterWidth = 1;
      this.splitContainer4.TabIndex = 0;
      this.splitContainer4.TabStop = false;
      //
      // splitContainer4.Panel1
      //
      this.splitContainer4.Panel1.Controls.Add(this.treeViewFast2);
      //
      // splitContainer4.Panel2
      //
      this.splitContainer4.Panel2.Controls.Add(this.dataGridView1);

      /////////////////////////////////////////////////////////////////////////////////////////////
      // LEFT PANEL
      //

      // 
      // btnStopRender
      // 
      this.btnStopRender.Cursor = System.Windows.Forms.Cursors.Default;
      this.btnStopRender.Enabled = false;
      this.btnStopRender.Location = new System.Drawing.Point(5, 6);
      this.btnStopRender.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      this.btnStopRender.Name = "btnStopRender";
      this.btnStopRender.Size = new System.Drawing.Size(80, 27);
      this.btnStopRender.TabIndex = 0;
      this.btnStopRender.Text = "Stop Render";
      this.btnStopRender.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
      this.btnStopRender.UseVisualStyleBackColor = true;
      this.btnStopRender.Click += new System.EventHandler(this.BtnStopRenderClick);
      // 
      // btnToggleData
      // 
      this.btnToggleData.Cursor = System.Windows.Forms.Cursors.Default;
      this.btnToggleData.Enabled = false;
      this.btnToggleData.Location = new System.Drawing.Point(89, 6);
      this.btnToggleData.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      this.btnToggleData.Name = "btnToggleData";
      this.btnToggleData.Size = new System.Drawing.Size(105, 27);
      this.btnToggleData.TabIndex = 1;
      this.btnToggleData.Text = "Hide Data Panel";
      this.btnToggleData.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
      this.btnToggleData.UseVisualStyleBackColor = true;
      this.btnToggleData.Click += new System.EventHandler(this.BtnHideDataClick);
      // 
      // btnExport
      // 
      this.btnExport.Cursor = System.Windows.Forms.Cursors.Default;
      this.btnExport.Enabled = false;
      this.btnExport.Location = new System.Drawing.Point(198, 6);
      this.btnExport.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      this.btnExport.Name = "btnExport";
      this.btnExport.Size = new System.Drawing.Size(71, 27);
      this.btnExport.TabIndex = 2;
      this.btnExport.Text = "Export";
      this.btnExport.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
      this.btnExport.UseVisualStyleBackColor = true;
      this.btnExport.Click += new System.EventHandler(this.BtnExportClick);
      // 
      // btnHelp
      // 
      this.btnHelp.Cursor = System.Windows.Forms.Cursors.Default;
      this.btnHelp.Enabled = false;
      this.btnHelp.Location = new System.Drawing.Point(273, 6);
      this.btnHelp.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      this.btnHelp.Name = "btnHelp";
      this.btnHelp.Size = new System.Drawing.Size(76, 27);
      this.btnHelp.TabIndex = 3;
      this.btnHelp.Text = "Help";
      this.btnHelp.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
      this.btnHelp.UseVisualStyleBackColor = true;
      this.btnHelp.Click += new System.EventHandler(this.BtnHelpClick);
      // 
      // treeViewFast1
      // 
      this.treeViewFast1.AfterSelect += new System.Windows.Forms.TreeViewEventHandler(this.TreeViewFast1AfterSelect);
      this.treeViewFast1.BorderStyle = System.Windows.Forms.BorderStyle.None;
      this.treeViewFast1.Dock = System.Windows.Forms.DockStyle.Bottom;
      this.treeViewFast1.ImageIndex = 0;
      this.treeViewFast1.ImageList = this.imageList1;
      this.treeViewFast1.Location = new System.Drawing.Point(0, 0);
      this.treeViewFast1.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      this.treeViewFast1.MouseHover += new System.EventHandler(this.TreeViewFast1MouseHover);
      this.treeViewFast1.MouseUp += new System.Windows.Forms.MouseEventHandler(this.TreeViewFast1MouseUp);
      this.treeViewFast1.Name = "treeViewFast1";
      this.treeViewFast1.SelectedImageIndex = 0;
      this.treeViewFast1.Size = new System.Drawing.Size(350, this.splitContainer2.Height - 60);
      this.treeViewFast1.TabIndex = 0;
      this.treeViewFast1.Visible = false;
      // 
      // imageList1
      // 
      this.imageList1.ColorDepth = System.Windows.Forms.ColorDepth.Depth8Bit;
      this.imageList1.ImageStream = ((System.Windows.Forms.ImageListStreamer)(resources.GetObject("imageList1.ImageStream")));
      this.imageList1.TransparentColor = System.Drawing.Color.Transparent;
      this.imageList1.Images.SetKeyName(0, "COMPUTER.ICO");
      this.imageList1.Images.SetKeyName(1, "Folder.ico");
      this.imageList1.Images.SetKeyName(2, "textdoc.ico");
      // 
      // contextMenuStrip1
      // 
      this.contextMenuStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
        this.toolStripMenuItem1
      });
      this.contextMenuStrip1.Name = "contextMenuStrip1";
      this.contextMenuStrip1.Size = new System.Drawing.Size(149, 26);
      // 
      // contextMenuStrip2
      // 
      this.contextMenuStrip2.Name = "contextMenuStrip2";
      this.contextMenuStrip2.Size = new System.Drawing.Size(61, 4);
      // 
      // toolStripMenuItem1
      // 
      this.toolStripMenuItem1.Name = "toolStripMenuItem1";
      this.toolStripMenuItem1.Size = new System.Drawing.Size(148, 22);
      this.toolStripMenuItem1.Text = "View All Items";
      this.toolStripMenuItem1.Click += new System.EventHandler(this.ToolStripMenuItem1Click);

      /////////////////////////////////////////////////////////////////////////////////////////////
      // CENTER PANEL
      //

      // 
      // loadingSwirl1
      // 
      this.loadingSwirl1.BackColor = System.Drawing.Color.White;
      this.loadingSwirl1.BorderStyle = System.Windows.Forms.BorderStyle.None;
      this.loadingSwirl1.Cursor = System.Windows.Forms.Cursors.Default;
      this.loadingSwirl1.Dock = System.Windows.Forms.DockStyle.Fill;
      this.loadingSwirl1.Image = global::PugTools.Properties.Resources.LoadingSwirl;
      this.loadingSwirl1.Location = new System.Drawing.Point(0, 0);
      this.loadingSwirl1.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      this.loadingSwirl1.Name = "loadingSwirl1";
      this.loadingSwirl1.Size = new System.Drawing.Size(1195, 1001);
      this.loadingSwirl1.SizeMode = System.Windows.Forms.PictureBoxSizeMode.CenterImage;
      this.loadingSwirl1.TabIndex = 0;
      this.loadingSwirl1.TabStop = false;
      // 
      // renderPanel
      // 
      this.renderPanel.BorderStyle = System.Windows.Forms.BorderStyle.None;
      this.renderPanel.Cursor = System.Windows.Forms.Cursors.Default;
      this.renderPanel.Dock = System.Windows.Forms.DockStyle.Fill;
      this.renderPanel.Location = new System.Drawing.Point(0, 0);
      this.renderPanel.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      this.renderPanel.MouseHover += new System.EventHandler(this.RenderPanelMouseHover);
      this.renderPanel.Name = "renderPanel";
      this.renderPanel.Resize += new System.EventHandler(this.RenderPanelResize);
      this.renderPanel.Size = new System.Drawing.Size(1195, 1001);
      this.renderPanel.TabIndex = 0;

      /////////////////////////////////////////////////////////////////////////////////////////////
      // RIGHT PANEL
      //

      // 
      // treeViewFast2
      // 
      this.treeViewFast2.BorderStyle = System.Windows.Forms.BorderStyle.None;
      this.treeViewFast2.Dock = System.Windows.Forms.DockStyle.Fill;
      this.treeViewFast2.Enabled = false;
      this.treeViewFast2.Location = new System.Drawing.Point(0, 0);
      this.treeViewFast2.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      this.treeViewFast2.Name = "treeViewFast2";
      this.treeViewFast2.Size = new System.Drawing.Size(364, 771);
      this.treeViewFast2.TabIndex = 0;
      this.treeViewFast2.AfterSelect += new System.Windows.Forms.TreeViewEventHandler(this.TreeViewFast2AfterSelect);
      this.treeViewFast2.MouseUp += new System.Windows.Forms.MouseEventHandler(this.TreeViewFast2MouseUp);
      // 
      // contextMenuStrip3
      // 
      this.contextMenuStrip3.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
        this.toolStripMenuItem2
      });
      this.contextMenuStrip3.Name = "contextMenuStrip3";
      this.contextMenuStrip3.Size = new System.Drawing.Size(150, 26);
      // 
      // toolStripMenuItem2
      // 
      this.toolStripMenuItem2.Name = "toolStripMenuItem2";
      this.toolStripMenuItem2.Size = new System.Drawing.Size(149, 22);
      this.toolStripMenuItem2.Text = "Toggle Render";
      this.toolStripMenuItem2.Click += new System.EventHandler(this.ToolStripMenuItem2Click);
      // 
      // contextMenuStrip4
      // 
      this.contextMenuStrip4.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
        this.toolStripMenuItem3
      });
      this.contextMenuStrip4.Name = "contextMenuStrip4";
      this.contextMenuStrip4.Size = new System.Drawing.Size(146, 26);
      // 
      // toolStripMenuItem3
      // 
      this.toolStripMenuItem3.Name = "toolStripMenuItem3";
      this.toolStripMenuItem3.Size = new System.Drawing.Size(145, 22);
      this.toolStripMenuItem3.Text = "View Material";
      this.toolStripMenuItem3.Click += new System.EventHandler(this.ToolStripMenuItem3Click);
      // 
      // dataGridView1
      // 
      this.dataGridView1.AllowUserToAddRows = false;
      this.dataGridView1.AllowUserToDeleteRows = false;
      this.dataGridView1.AllowUserToResizeRows = false;
      this.dataGridView1.BorderStyle = System.Windows.Forms.BorderStyle.None;
      this.dataGridView1.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
      this.dataGridView1.Cursor = System.Windows.Forms.Cursors.Default;
      this.dataGridView1.Dock = System.Windows.Forms.DockStyle.Fill;
      this.dataGridView1.Enabled = false;
      this.dataGridView1.Location = new System.Drawing.Point(0, 0);
      this.dataGridView1.Margin = new System.Windows.Forms.Padding(4, 3, 4, 3);
      this.dataGridView1.Name = "dataGridView1";
      this.dataGridView1.RowHeadersVisible = false;
      this.dataGridView1.Size = new System.Drawing.Size(364, 221);
      this.dataGridView1.TabIndex = 0;

      /////////////////////////////////////////////////////////////////////////////////////////////
      // STATUS BAR
      //

      // 
      // statusStrip1
      // 
      this.statusStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
        this.toolStripStatusLabel1,
        this.toolStripProgressBar1
      });
      this.statusStrip1.Location = new System.Drawing.Point(0, 1013);
      this.statusStrip1.Name = "statusStrip1";
      this.statusStrip1.Padding = new System.Windows.Forms.Padding(1, 0, 16, 0);
      this.statusStrip1.Size = new System.Drawing.Size(this.splitContainer1.Width, 22);
      this.statusStrip1.TabIndex = 1;
      this.statusStrip1.Text = "statusStrip1";
      // 
      // toolStripStatusLabel1
      // 
      this.toolStripStatusLabel1.Name = "toolStripStatusLabel1";
      this.toolStripStatusLabel1.Size = new System.Drawing.Size(0, 19);
      // 
      // toolStripProgressBar1
      // 
      this.toolStripProgressBar1.ForeColor = System.Drawing.Color.Lime;
      this.toolStripProgressBar1.Name = "toolStripProgressBar1";
      this.toolStripProgressBar1.Size = new System.Drawing.Size(117, 18);
      this.toolStripProgressBar1.Style = System.Windows.Forms.ProgressBarStyle.Marquee;

      /////////////////////////////////////////////////////////////////////////////////////////////
      // BACKGROUND

      // 
      // backgroundWorker1
      // 
      this.backgroundWorker1.WorkerSupportsCancellation = true;
      this.backgroundWorker1.DoWork += new System.ComponentModel.DoWorkEventHandler(this.BackgroundWorker1DoWork);
      this.backgroundWorker1.RunWorkerCompleted += new System.ComponentModel.RunWorkerCompletedEventHandler(this.BackgroundWorker1RunWorkerCompleted);

      // 
      // backgroundWorker2
      // 
      this.backgroundWorker2.DoWork += new System.ComponentModel.DoWorkEventHandler(this.BackgroundWorker2DoWork);
      this.backgroundWorker2.ProgressChanged += new System.ComponentModel.ProgressChangedEventHandler(this.BackgroundWorker2ProgressChanged);
      this.backgroundWorker2.RunWorkerCompleted += new System.ComponentModel.RunWorkerCompletedEventHandler(this.BackgroundWorker2Completed);
      this.backgroundWorker2.WorkerReportsProgress = true;
      this.backgroundWorker2.WorkerSupportsCancellation = true;

      // 
      // backgroundWorker3
      // 
      this.backgroundWorker3.WorkerSupportsCancellation = true;
      this.backgroundWorker3.DoWork += new System.ComponentModel.DoWorkEventHandler(this.BackgroundWorker3Run);
      this.backgroundWorker3.RunWorkerCompleted += new System.ComponentModel.RunWorkerCompletedEventHandler(this.BackgroundWorker3RunWorkerCompleted);

      /////////////////////////////////////////////////////////////////////////////////////////////
      // FORM
      //

      // 
      // ModelBrowser
      // 
      this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
      this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
      this.ClientSize = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea.Size;
      this.Controls.Add(this.splitContainer1);
      this.FormClosed += new System.Windows.Forms.FormClosedEventHandler(this.ModelBrowserFormClosed);
      this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.ModelBrowserFormClosing);
      this.Margin = new System.Windows.Forms.Padding(0, 0, 0, 0);
      this.Name = "ModelBrowser";
      this.Resize += new System.EventHandler(this.ModelBrowserFormResize);
      this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
      this.Text = "Model Browser";
      this.WindowState = System.Windows.Forms.FormWindowState.Maximized;
      //
      this.splitContainer1.Panel1.ResumeLayout(false);
      this.splitContainer1.Panel2.ResumeLayout(false);
      ((System.ComponentModel.ISupportInitialize)(this.splitContainer1)).EndInit();
      this.splitContainer1.ResumeLayout(false);
      this.splitContainer2.Panel1.ResumeLayout(false);
      this.splitContainer2.Panel2.ResumeLayout(false);
      ((System.ComponentModel.ISupportInitialize)(this.splitContainer2)).EndInit();
      this.splitContainer2.ResumeLayout(false);
      this.splitContainer3.Panel1.ResumeLayout(false);
      this.splitContainer3.Panel1.PerformLayout();
      this.splitContainer3.Panel2.ResumeLayout(false);
      ((System.ComponentModel.ISupportInitialize)(this.splitContainer3)).EndInit();
      this.splitContainer3.ResumeLayout(false);
      this.splitContainer4.Panel1.ResumeLayout(false);
      this.splitContainer4.Panel1.PerformLayout();
      this.splitContainer4.Panel2.ResumeLayout(false);
      ((System.ComponentModel.ISupportInitialize)(this.splitContainer4)).EndInit();
      this.splitContainer4.ResumeLayout(false);
      //
      ((System.ComponentModel.ISupportInitialize)(this.dataGridView1)).EndInit();
      ((System.ComponentModel.ISupportInitialize)(this.loadingSwirl1)).EndInit();
      this.contextMenuStrip1.ResumeLayout(false);
      this.contextMenuStrip2.ResumeLayout(false);
      this.contextMenuStrip3.ResumeLayout(false);
      this.contextMenuStrip4.ResumeLayout(false);
      this.statusStrip1.ResumeLayout(false);
      this.statusStrip1.PerformLayout();
      this.ResumeLayout(false);
    }

    #endregion

    private System.Windows.Forms.SplitContainer splitContainer1;
    private System.Windows.Forms.SplitContainer splitContainer2;
    private System.Windows.Forms.SplitContainer splitContainer3;
    private System.Windows.Forms.SplitContainer splitContainer4;
    private System.Windows.Forms.Button btnStopRender;
    private System.Windows.Forms.Button btnToggleData;
    private System.Windows.Forms.Button btnExport;
    private System.Windows.Forms.Button btnHelp;
    private TreeViewFast.Controls.TreeViewFast treeViewFast1;
    private System.Windows.Forms.ContextMenuStrip contextMenuStrip2;
    private System.Windows.Forms.ContextMenuStrip contextMenuStrip1;
    private System.Windows.Forms.ToolStripMenuItem toolStripMenuItem1;
    private System.Windows.Forms.ImageList imageList1;
    private System.Windows.Forms.PictureBox loadingSwirl1;
    private System.Windows.Forms.Panel renderPanel;
    private TreeViewFast.Controls.TreeViewFast treeViewFast2;
    private System.Windows.Forms.ContextMenuStrip contextMenuStrip3;
    private System.Windows.Forms.ToolStripMenuItem toolStripMenuItem2;
    private System.Windows.Forms.ContextMenuStrip contextMenuStrip4;
    private System.Windows.Forms.ToolStripMenuItem toolStripMenuItem3;
    private System.Windows.Forms.DataGridView dataGridView1;
    private System.Windows.Forms.StatusStrip statusStrip1;
    private System.Windows.Forms.ToolStripStatusLabel toolStripStatusLabel1;
    private System.Windows.Forms.ToolStripProgressBar toolStripProgressBar1;
    private System.ComponentModel.BackgroundWorker backgroundWorker1;
    private System.ComponentModel.BackgroundWorker backgroundWorker2;
    private System.ComponentModel.BackgroundWorker backgroundWorker3;
  }
}
