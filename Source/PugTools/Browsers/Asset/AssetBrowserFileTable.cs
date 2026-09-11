using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using nsHashDictionary;
using TorArchive;

namespace PugTools {
  internal partial class AssetBrowserFileTable : Form {
    private Dictionary<String, ArchTreeListItem> _assetDict; // = new Dictionary<string, ArchTreeListItem>();
    private Boolean _closing;
    private Dictionary<String, Int32> _currentArchDetails; // = new Dictionary<string, int>();
    private Archive _currentArchive;
    private Dictionary<String, List<FileInfo>> _fileDict; // = s_dictionaries;
    private UInt32 _filesNamed; // = 0;
    private UInt32 _filesTotal; // = 0;
    private UInt32 _filesUnnamed; // = 0;
    private readonly HashDictionaryInstance _hashData;
    private ArrayList _rootList; // = new ArrayList();

    public override Boolean AllowDrop {
      get => base.AllowDrop;
      set => base.AllowDrop = value;
    }
    public override AnchorStyles Anchor {
      get => base.Anchor;
      set => base.Anchor = value;
    }

    internal AssetBrowserFileTable() {
      InitializeComponent();

      FormClosed += AssetBrowserFileTable_FormClosed; // Move this to Designer?
      _hashData = HashDictionaryInstance.Instance;

      if (!_hashData.Loaded) _hashData.Load();

      ShowLoader();

      treeListView1.CanExpandGetter = delegate (Object x) {
        if (x.GetType() == typeof(NodeListItem))
          return ((NodeListItem)x).children.Count > 0;

        return false;
      };

      treeListView1.ChildrenGetter = delegate (Object x) {
        if (x.GetType() == typeof(NodeListItem))
          return new ArrayList(((NodeListItem)x).children);

        return null;
      };

      backgroundWorker1.RunWorkerAsync();
    }
    public void AssetBrowserFileTable_FormClosed(Object sender, FormClosedEventArgs e) {
      // The hash dictionary is a process-wide cache used by all browser windows. This child
      // window only drops its own UI data; unloading the shared cache here races other browsers.

      // Designer-owned tree is disposed by Form.Dispose(); avoid a second deep subtree walk.
      treeViewFast1 = null;

      _assetDict = null;
      _fileDict = null;
    }
    private void AssetBrowserFileTable_FormClosing(Object sender, FormClosingEventArgs e) {
      _closing = true;
      try { if (backgroundWorker1.WorkerSupportsCancellation && backgroundWorker1.IsBusy) backgroundWorker1.CancelAsync(); } catch { }
    }
    protected override void AdjustFormScrollbars(Boolean displayScrollbars) {
      base.AdjustFormScrollbars(displayScrollbars);
    }
    #region Background Wokers Methods
    private void BackgroundWorker1_DoWork(Object sender, DoWorkEventArgs e) {
      if (_closing) return;

      _assetDict ??= new Dictionary<String, ArchTreeListItem>();

      Assets currentAssets = AssetHandler.Instance.GetCurrentAssets();

      foreach (Library lib in currentAssets.Libraries) {
        String path = lib.Location;

        if (!lib.Loaded) lib.Load();

        if (lib.Archives.Count > 0) {
          foreach (KeyValuePair<Int32, Archive> arch in lib.Archives) {
            String name = arch.Value.FileName.Split('\\').Last().Replace(".tor", "");
            ArchTreeListItem assetArch = new ArchTreeListItem(
              "/root/" + name, "/root", name, arch.Value
            );
            _assetDict["/root/" + name] = assetArch;
          }
        }
      }

      _assetDict.Add("/root", new ArchTreeListItem("/root", string.Empty, "Root", null));
    }

    private void BackgroundWorker1_RunWorkerCompleted(Object sender,
                                                      RunWorkerCompletedEventArgs e) {
      if (_closing) return;

      toolStripStatusLabel1.Text = "Loading Tree View Items ...";

      static String getId(ArchTreeListItem x) => x.Id;
      static String getParentId(ArchTreeListItem x) => x.ParentId;
      static String getDisplayName(ArchTreeListItem x) => x.DisplayName;

      treeViewFast1.BeginUpdate();
      treeViewFast1.LoadItems<ArchTreeListItem>(_assetDict, getId, getParentId, getDisplayName);
      treeViewFast1.EndUpdate();

      toolStripStatusLabel1.Text = "Loading Complete";
      treeViewFast1.Visible = true;

      HideLoader();
      EnableUI();

      if (treeViewFast1.Nodes.Count > 0) treeViewFast1.Nodes[0].Expand();
    }
    #endregion
    protected override AccessibleObject CreateAccessibilityInstance() {
      return base.CreateAccessibilityInstance();
    }
    protected override Control.ControlCollection CreateControlsInstance() {
      return base.CreateControlsInstance();
    }
    protected override void CreateHandle() {
      base.CreateHandle();
    }
    // public override ObjRef CreateObjRef(Type requestedType) {
    //   return base.CreateObjRef(requestedType);
    // }
    protected override void DefWndProc(ref Message m) {
      base.DefWndProc(ref m);
    }
    protected override void DestroyHandle() {
      base.DestroyHandle();
    }
    private void EnableUI() {
      dataGridView1.Enabled = true;
      treeViewFast1.Enabled = true;
      treeListView1.Enabled = true;
    }
    public override Boolean Equals(Object obj) {
      return obj is AssetBrowserFileTable table
             && EqualityComparer<Dictionary<String, List<FileInfo>>>.Default.Equals(
               _fileDict,
               table._fileDict
             );
    }
    protected override AccessibleObject GetAccessibilityObjectById(Int32 objectId) {
      return base.GetAccessibilityObjectById(objectId);
    }
    public override Int32 GetHashCode() {
      return base.GetHashCode();
    }
    public override Size GetPreferredSize(Size proposedSize) {
      return base.GetPreferredSize(proposedSize);
    }
    protected override Rectangle GetScaledBounds(Rectangle bounds,
                                                 SizeF factor,
                                                 BoundsSpecified specified) {
      return base.GetScaledBounds(bounds, factor, specified);
    }
    protected override Object GetService(Type service) {
      return base.GetService(service);
    }
    public void HideLoader() {
      loadingSwirl1.Visible = false;
      toolStripProgressBar1.Visible = false;
    }
    public void HideViewers() {
      treeListView1.Visible = false;
    }
    [Obsolete("Intellisense told me to do this!")]
    public override Object InitializeLifetimeService() {
      return base.InitializeLifetimeService();
    }
    protected override void InitLayout() {
      base.InitLayout();
    }
    protected override Boolean IsInputChar(Char charCode) {
      return base.IsInputChar(charCode);
    }
    protected override Boolean IsInputKey(Keys keyData) {
      return base.IsInputKey(keyData);
    }
    protected override void NotifyInvalidate(Rectangle invalidatedArea) {
      base.NotifyInvalidate(invalidatedArea);
    }
    protected override void OnActivated(EventArgs e) {
      base.OnActivated(e);
    }
    protected override void OnAutoSizeChanged(EventArgs e) {
      base.OnAutoSizeChanged(e);
    }
    protected override void OnAutoValidateChanged(EventArgs e) {
      base.OnAutoValidateChanged(e);
    }
    protected override void OnBackColorChanged(EventArgs e) {
      base.OnBackColorChanged(e);
    }
    protected override void OnBackgroundImageChanged(EventArgs e) {
      base.OnBackgroundImageChanged(e);
    }
    protected override void OnBackgroundImageLayoutChanged(EventArgs e) {
      base.OnBackgroundImageLayoutChanged(e);
    }
    protected override void OnBindingContextChanged(EventArgs e) {
      base.OnBindingContextChanged(e);
    }
    protected override void OnCausesValidationChanged(EventArgs e) {
      base.OnCausesValidationChanged(e);
    }
    protected override void OnChangeUICues(UICuesEventArgs e) {
      base.OnChangeUICues(e);
    }
    protected override void OnClick(EventArgs e) {
      base.OnClick(e);
    }
    protected override void OnClientSizeChanged(EventArgs e) {
      base.OnClientSizeChanged(e);
    }
    protected override void OnClosed(EventArgs e) {
      base.OnClosed(e);
    }
    protected override void OnClosing(CancelEventArgs e) {
      base.OnClosing(e);
    }
    // protected override void OnContextMenuChanged(EventArgs e) {
    //   base.OnContextMenuChanged(e);
    // }
    protected override void OnContextMenuStripChanged(EventArgs e) {
      base.OnContextMenuStripChanged(e);
    }
    protected override void OnControlAdded(ControlEventArgs e) {
      base.OnControlAdded(e);
    }
    protected override void OnControlRemoved(ControlEventArgs e) {
      base.OnControlRemoved(e);
    }
    protected override void OnCreateControl() {
      base.OnCreateControl();
    }
    protected override void OnCursorChanged(EventArgs e) {
      base.OnCursorChanged(e);
    }
    protected override void OnDeactivate(EventArgs e) {
      base.OnDeactivate(e);
    }
    protected override void OnDockChanged(EventArgs e) {
      base.OnDockChanged(e);
    }
    protected override void OnDoubleClick(EventArgs e) {
      base.OnDoubleClick(e);
    }
    protected override void OnDragDrop(DragEventArgs drgevent) {
      base.OnDragDrop(drgevent);
    }
    protected override void OnDragEnter(DragEventArgs drgevent) {
      base.OnDragEnter(drgevent);
    }
    protected override void OnDragLeave(EventArgs e) {
      base.OnDragLeave(e);
    }
    protected override void OnDragOver(DragEventArgs drgevent) {
      base.OnDragOver(drgevent);
    }
    protected override void OnEnabledChanged(EventArgs e) {
      base.OnEnabledChanged(e);
    }
    protected override void OnEnter(EventArgs e) {
      base.OnEnter(e);
    }
    protected override void OnFontChanged(EventArgs e) {
      base.OnFontChanged(e);
    }
    protected override void OnForeColorChanged(EventArgs e) {
      base.OnForeColorChanged(e);
    }
    protected override void OnFormClosed(FormClosedEventArgs e) {
      base.OnFormClosed(e);
    }
    protected override void OnFormClosing(FormClosingEventArgs e) {
      base.OnFormClosing(e);
    }
    protected override void OnGiveFeedback(GiveFeedbackEventArgs gfbevent) {
      base.OnGiveFeedback(gfbevent);
    }
    protected override void OnGotFocus(EventArgs e) {
      base.OnGotFocus(e);
    }
    protected override void OnHandleCreated(EventArgs e) {
      base.OnHandleCreated(e);
    }
    protected override void OnHandleDestroyed(EventArgs e) {
      base.OnHandleDestroyed(e);
    }
    protected override void OnHelpButtonClicked(CancelEventArgs e) {
      base.OnHelpButtonClicked(e);
    }
    protected override void OnHelpRequested(HelpEventArgs hevent) {
      base.OnHelpRequested(hevent);
    }
    protected override void OnImeModeChanged(EventArgs e) {
      base.OnImeModeChanged(e);
    }
    protected override void OnInputLanguageChanged(InputLanguageChangedEventArgs e) {
      base.OnInputLanguageChanged(e);
    }
    protected override void OnInputLanguageChanging(InputLanguageChangingEventArgs e) {
      base.OnInputLanguageChanging(e);
    }
    protected override void OnInvalidated(InvalidateEventArgs e) {
      base.OnInvalidated(e);
    }
    protected override void OnKeyDown(KeyEventArgs e) {
      base.OnKeyDown(e);
    }
    protected override void OnKeyPress(KeyPressEventArgs e) {
      base.OnKeyPress(e);
    }
    protected override void OnKeyUp(KeyEventArgs e) {
      base.OnKeyUp(e);
    }
    protected override void OnLayout(LayoutEventArgs levent) {
      base.OnLayout(levent);
    }
    protected override void OnLeave(EventArgs e) {
      base.OnLeave(e);
    }
    protected override void OnLoad(EventArgs e) {
      base.OnLoad(e);
    }
    protected override void OnLocationChanged(EventArgs e) {
      base.OnLocationChanged(e);
    }
    protected override void OnLostFocus(EventArgs e) {
      base.OnLostFocus(e);
    }
    protected override void OnMarginChanged(EventArgs e) {
      base.OnMarginChanged(e);
    }
    protected override void OnMaximizedBoundsChanged(EventArgs e) {
      base.OnMaximizedBoundsChanged(e);
    }
    protected override void OnMaximumSizeChanged(EventArgs e) {
      base.OnMaximumSizeChanged(e);
    }
    protected override void OnMdiChildActivate(EventArgs e) {
      base.OnMdiChildActivate(e);
    }
    protected override void OnMenuComplete(EventArgs e) {
      base.OnMenuComplete(e);
    }
    protected override void OnMenuStart(EventArgs e) {
      base.OnMenuStart(e);
    }
    protected override void OnMinimumSizeChanged(EventArgs e) {
      base.OnMinimumSizeChanged(e);
    }
    protected override void OnMouseCaptureChanged(EventArgs e) {
      base.OnMouseCaptureChanged(e);
    }
    protected override void OnMouseClick(MouseEventArgs e) {
      base.OnMouseClick(e);
    }
    protected override void OnMouseDoubleClick(MouseEventArgs e) {
      base.OnMouseDoubleClick(e);
    }
    protected override void OnMouseDown(MouseEventArgs e) {
      base.OnMouseDown(e);
    }
    protected override void OnMouseEnter(EventArgs e) {
      base.OnMouseEnter(e);
    }
    protected override void OnMouseHover(EventArgs e) {
      base.OnMouseHover(e);
    }
    protected override void OnMouseLeave(EventArgs e) {
      base.OnMouseLeave(e);
    }
    protected override void OnMouseMove(MouseEventArgs e) {
      base.OnMouseMove(e);
    }
    protected override void OnMouseUp(MouseEventArgs e) {
      base.OnMouseUp(e);
    }
    protected override void OnMouseWheel(MouseEventArgs e) {
      base.OnMouseWheel(e);
    }
    protected override void OnMove(EventArgs e) {
      base.OnMove(e);
    }
    protected override void OnNotifyMessage(Message m) {
      base.OnNotifyMessage(m);
    }
    protected override void OnPaddingChanged(EventArgs e) {
      base.OnPaddingChanged(e);
    }
    protected override void OnPaint(PaintEventArgs e) {
      base.OnPaint(e);
    }
    protected override void OnPaintBackground(PaintEventArgs e) {
      base.OnPaintBackground(e);
    }
    protected override void OnParentBackColorChanged(EventArgs e) {
      base.OnParentBackColorChanged(e);
    }
    protected override void OnParentBackgroundImageChanged(EventArgs e) {
      base.OnParentBackgroundImageChanged(e);
    }
    protected override void OnParentBindingContextChanged(EventArgs e) {
      base.OnParentBindingContextChanged(e);
    }
    protected override void OnParentChanged(EventArgs e) {
      base.OnParentChanged(e);
    }
    protected override void OnParentCursorChanged(EventArgs e) {
      base.OnParentCursorChanged(e);
    }
    protected override void OnParentEnabledChanged(EventArgs e) {
      base.OnParentEnabledChanged(e);
    }
    protected override void OnParentFontChanged(EventArgs e) {
      base.OnParentFontChanged(e);
    }
    protected override void OnParentForeColorChanged(EventArgs e) {
      base.OnParentForeColorChanged(e);
    }
    protected override void OnParentRightToLeftChanged(EventArgs e) {
      base.OnParentRightToLeftChanged(e);
    }
    protected override void OnParentVisibleChanged(EventArgs e) {
      base.OnParentVisibleChanged(e);
    }
    protected override void OnPreviewKeyDown(PreviewKeyDownEventArgs e) {
      base.OnPreviewKeyDown(e);
    }
    protected override void OnPrint(PaintEventArgs e) {
      base.OnPrint(e);
    }
    protected override void OnQueryContinueDrag(QueryContinueDragEventArgs qcdevent) {
      base.OnQueryContinueDrag(qcdevent);
    }
    protected override void OnRegionChanged(EventArgs e) {
      base.OnRegionChanged(e);
    }
    protected override void OnResize(EventArgs e) {
      base.OnResize(e);
    }
    protected override void OnResizeBegin(EventArgs e) {
      base.OnResizeBegin(e);
    }
    protected override void OnResizeEnd(EventArgs e) {
      base.OnResizeEnd(e);
    }
    protected override void OnRightToLeftChanged(EventArgs e) {
      base.OnRightToLeftChanged(e);
    }
    protected override void OnRightToLeftLayoutChanged(EventArgs e) {
      base.OnRightToLeftLayoutChanged(e);
    }
    protected override void OnScroll(ScrollEventArgs se) {
      base.OnScroll(se);
    }
    protected override void OnShown(EventArgs e) {
      base.OnShown(e);
    }
    protected override void OnSizeChanged(EventArgs e) {
      base.OnSizeChanged(e);
    }
    protected override void OnStyleChanged(EventArgs e) {
      base.OnStyleChanged(e);
    }
    protected override void OnSystemColorsChanged(EventArgs e) {
      base.OnSystemColorsChanged(e);
    }
    protected override void OnTabIndexChanged(EventArgs e) {
      base.OnTabIndexChanged(e);
    }
    protected override void OnTabStopChanged(EventArgs e) {
      base.OnTabStopChanged(e);
    }
    protected override void OnTextChanged(EventArgs e) {
      base.OnTextChanged(e);
    }
    protected override void OnValidated(EventArgs e) {
      base.OnValidated(e);
    }
    protected override void OnValidating(CancelEventArgs e) {
      base.OnValidating(e);
    }
    protected override void OnVisibleChanged(EventArgs e) {
      base.OnVisibleChanged(e);
    }
    private void ParseLibFiles() {
      if (InvokeRequired) Invoke(new Action(() => ParseLibFiles()));
      else {
        List<File> files = _currentArchive.EnumerateFiles().ToList();
        files.Sort(delegate (File x, File y) {
          return (x.FileInfo.Offset > y.FileInfo.Offset) ? -1 : 1;
        });

        _rootList ??= new ArrayList();

        foreach (File file in files) {
          HashFileInfo hashInfo =
            new HashFileInfo(file.FileInfo.PrimaryHash, file.FileInfo.SecondaryHash, file, true, false);

          if (hashInfo.FileName.Contains("metadata.bin") || hashInfo.FileName.Contains("ft.sig"))
            continue;

          _filesTotal++;

          if (hashInfo.IsNamed)
            _filesNamed++;
          else
            _filesUnnamed++;

          if (!_currentArchDetails.Keys.Contains(hashInfo.Extension))
            _currentArchDetails.Add(hashInfo.Extension, 0);
          else
            _currentArchDetails[hashInfo.Extension]++;

          _rootList.Add(new FileListItem(hashInfo, file.FileInfo));
        }
      }
    }
    public override Boolean PreProcessMessage(ref Message msg) {
      return base.PreProcessMessage(ref msg);
    }
    protected override Boolean ProcessCmdKey(ref Message msg, Keys keyData) {
      return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override Boolean ProcessDialogChar(Char charCode) {
      return base.ProcessDialogChar(charCode);
    }
    protected override Boolean ProcessDialogKey(Keys keyData) {
      return base.ProcessDialogKey(keyData);
    }
    protected override Boolean ProcessKeyEventArgs(ref Message m) {
      return base.ProcessKeyEventArgs(ref m);
    }
    protected override Boolean ProcessKeyMessage(ref Message m) {
      return base.ProcessKeyMessage(ref m);
    }
    protected override Boolean ProcessKeyPreview(ref Message m) {
      return base.ProcessKeyPreview(ref m);
    }
    protected override Boolean ProcessMnemonic(Char charCode) {
      return base.ProcessMnemonic(charCode);
    }
    protected override Boolean ProcessTabKey(Boolean forward) {
      return base.ProcessTabKey(forward);
    }
    public override void Refresh() {
      base.Refresh();
    }
    public override void ResetBackColor() {
      base.ResetBackColor();
    }
    public override void ResetCursor() {
      base.ResetCursor();
    }
    public override void ResetFont() {
      base.ResetFont();
    }
    public override void ResetForeColor() {
      base.ResetForeColor();
    }
    public override void ResetRightToLeft() {
      base.ResetRightToLeft();
    }
    public override void ResetText() {
      base.ResetText();
    }
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified) {
      base.ScaleControl(factor, specified);
    }
    protected override void ScaleCore(Single x, Single y) {
      base.ScaleCore(x, y);
    }
    protected override Point ScrollToControl(Control activeControl) {
      return base.ScrollToControl(activeControl);
    }
    protected override void Select(Boolean directed, Boolean forward) {
      base.Select(directed, forward);
    }
    protected override void SetBoundsCore(Int32 x,
                                          Int32 y,
                                          Int32 width,
                                          Int32 height,
                                          BoundsSpecified specified) {
      base.SetBoundsCore(x, y, width, height, specified);
    }
    protected override void SetClientSizeCore(Int32 x, Int32 y) {
      base.SetClientSizeCore(x, y);
    }
    public void SetStatusLabel(String message) {
      if (statusStrip1.InvokeRequired) {
        statusStrip1.Invoke(new Action(() => SetStatusLabel(message)));
      } else {
        toolStripStatusLabel1.Text = message;
      }
    }
    private void SetStripProgressBarMax(Int32 prog) {
      if (InvokeRequired) {
        Invoke(new Action<Int32>(SetStripProgressBarMax), new Object[] { prog });
        return;
      }

      toolStripProgressBar1.Maximum = prog;
    }
    private void SetStripProgressBarStyle(ProgressBarStyle style) {
      if (InvokeRequired) {
        Invoke(new Action<ProgressBarStyle>(SetStripProgressBarStyle), new Object[] { style });
        return;
      }

      toolStripProgressBar1.Style = style;
    }
    private void SetStripProgressBarValue(Int32 prog) {
      if (InvokeRequired) {
        Invoke(new Action<Int32>(SetStripProgressBarValue), new Object[] { prog });
        return;
      }

      toolStripProgressBar1.Value = prog;
    }
    protected override void SetVisibleCore(Boolean value) {
      base.SetVisibleCore(value);
    }
    public void ShowLoader() {
      loadingSwirl1.Visible = true;
      toolStripProgressBar1.Visible = true;
    }
    protected override Size SizeFromClientSize(Size clientSize) {
      return base.SizeFromClientSize(clientSize);
    }
    public override String ToString() {
      return base.ToString();
    }
    private async void TreeViewFast1_AfterSelect(Object sender, TreeViewEventArgs e) {
      TreeNode node = treeViewFast1.SelectedNode;
      ArchTreeListItem obj = (ArchTreeListItem)node.Tag;
      Text = "Asset File Table Browser  - " + obj.Id.ToString();

      if (obj.Arch != null) {
        HideViewers();
        ShowLoader();

        DataTable dt = new DataTable();
        _currentArchive = obj.Arch;
        _currentArchDetails = new Dictionary<String, Int32>();
        _filesTotal = 0;
        _filesNamed = 0;
        _filesUnnamed = 0;
        // _filesMissing = 0;

        FileListItem.ResetTreeListViewColumns(treeListView1);

        treeListView1.TopItemIndex = 0;

        if (_rootList != null) _rootList.Clear();

        await Task.Run(() => ParseLibFiles());
        HideLoader();

        treeListView1.Roots = _rootList;

        treeListView1.Sort(
          (BrightIdeasSoftware.OLVColumn)treeListView1.Columns[2],
          SortOrder.Ascending
        );

        treeListView1.AutoResizeColumn(0, ColumnHeaderAutoResizeStyle.ColumnContent);
        treeListView1.AutoResizeColumn(1, ColumnHeaderAutoResizeStyle.HeaderSize);
        treeListView1.AutoResizeColumn(2, ColumnHeaderAutoResizeStyle.ColumnContent);
        treeListView1.AutoResizeColumn(3, ColumnHeaderAutoResizeStyle.HeaderSize);
        treeListView1.AutoResizeColumn(4, ColumnHeaderAutoResizeStyle.HeaderSize);
        treeListView1.AutoResizeColumn(5, ColumnHeaderAutoResizeStyle.HeaderSize);
        treeListView1.AutoResizeColumn(6, ColumnHeaderAutoResizeStyle.HeaderSize);
        treeListView1.AutoResizeColumn(7, ColumnHeaderAutoResizeStyle.HeaderSize);

        treeListView1.Visible = true;

        Double dblCompletion = _filesNamed / (Double)_filesTotal;

        dt.Columns.Add("Property");
        dt.Columns.Add("Value");

        dt.Rows.Add(
          new String[] { "Archive", obj.Arch.FileName.Split('\\').Last().Replace(".tor", "") }
        );
        dt.Rows.Add(
          new String[] { "Total Files", String.Format("{0:n0}", _filesTotal.ToString()) }
        );
        dt.Rows.Add(
          new String[] { "Named Files", String.Format("{0:n0}", _filesNamed.ToString()) }
        );
        dt.Rows.Add(
          new String[] { "Unnamed Files", String.Format("{0:n0}", _filesUnnamed.ToString()) }
        );
        dt.Rows.Add(
          new String[] { "Name Completion", String.Format("{0:0.0%}", dblCompletion) }
        );

        if (_currentArchDetails.Count > 0) {
          List<String> keys = _currentArchDetails.Keys.ToList();
          keys.Sort();

          foreach (String key in keys) {
            dt.Rows.Add(
              new String[] { key.ToUpper() + " Files", _currentArchDetails[key].ToString() }
            );
          }
        }

        dataGridView1.DataSource = dt;
        dataGridView1.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
      }
    }
    protected override void UpdateDefaultButton() {
      base.UpdateDefaultButton();
    }
    public override Boolean ValidateChildren() {
      return base.ValidateChildren();
    }
    public override Boolean ValidateChildren(ValidationConstraints validationConstraints) {
      return base.ValidateChildren(validationConstraints);
    }
    protected override void WndProc(ref Message m) {
      base.WndProc(ref m);
    }
    /*
    private readonly UInt32 _filesMissing; // = 0;
    private readonly HashSet<String> _foundFiles; // = hashSets;
    private readonly Hasher _hasher; // = hasher1;

    private static readonly HashSet<String> s_hashSets; // = new HashSet<string>();
    private static readonly Dictionary<String, List<FileInfo>> s_dictionaries; // = new Dictionary<string, List<FileInfo>>();
    private static readonly Hasher s_hasher1; // = new Hasher(Hasher.HasherType.TOR);

    protected override bool DoubleBuffered {
      get => base.DoubleBuffered;
      set => base.DoubleBuffered = value;
    }
    protected override ImeMode ImeModeBase {
      get => base.ImeModeBase;
      set => base.ImeModeBase = value;
    }

    public override Size AutoScaleBaseSize {
      get => base.AutoScaleBaseSize;
      set => base.AutoScaleBaseSize = value;
    }
    public override Point AutoScrollOffset {
      get => base.AutoScrollOffset;
      set => base.AutoScrollOffset = value;
    }
    public override Boolean AutoScroll {
      get => base.AutoScroll;
      set => base.AutoScroll = value;
    }
    public override Boolean AutoSize {
      get => base.AutoSize;
      set => base.AutoSize = value;
    }
    public override AutoValidate AutoValidate {
      get => base.AutoValidate;
      set => base.AutoValidate = value;
    }
    public override Image BackgroundImage {
      get => base.BackgroundImage;
      set => base.BackgroundImage = value;
    }
    public override ImageLayout BackgroundImageLayout {
      get => base.BackgroundImageLayout;
      set => base.BackgroundImageLayout = value;
    }
    public override Color BackColor {
      get => base.BackColor;
      set => base.BackColor = value;
    }
    public override BindingContext BindingContext {
      get => base.BindingContext;
      set => base.BindingContext = value;
    }
    public override ContextMenu ContextMenu {
      get => base.ContextMenu;
      set => base.ContextMenu = value;
    }
    public override ContextMenuStrip ContextMenuStrip {
      get => base.ContextMenuStrip;
      set => base.ContextMenuStrip = value;
    }
    public override Cursor Cursor {
      get => base.Cursor;
      set => base.Cursor = value;
    }
    public override DockStyle Dock {
      get => base.Dock;
      set => base.Dock = value;
    }
    public override Font Font {
      get => base.Font;
      set => base.Font = value;
    }
    public override Color ForeColor {
      get => base.ForeColor;
      set => base.ForeColor = value;
    }
    public override Size MaximumSize {
      get => base.MaximumSize;
      set => base.MaximumSize = value;
    }
    public override Size MinimumSize {
      get => base.MinimumSize;
      set => base.MinimumSize = value;
    }
    public override RightToLeft RightToLeft {
      get => base.RightToLeft;
      set => base.RightToLeft = value;
    }
    public override bool RightToLeftLayout { 
      get => base.RightToLeftLayout; 
      set => base.RightToLeftLayout = value; 
    }
    public override ISite Site { 
      get => base.Site; 
      set => base.Site = value; 
    }
    public override string Text { 
      get => base.Text; 
      set => base.Text = value; 
    }

    protected override Boolean CanEnableIme => base.CanEnableIme;
    protected override Boolean CanRaiseEvents => base.CanRaiseEvents;
    protected override CreateParams CreateParams => base.CreateParams;
    protected override Cursor DefaultCursor => base.DefaultCursor;
    protected override ImeMode DefaultImeMode => base.DefaultImeMode;
    protected override Padding DefaultMargin => base.DefaultMargin;
    protected override Size DefaultMaximumSize => base.DefaultMaximumSize;
    protected override Size DefaultMinimumSize => base.DefaultMinimumSize;
    protected override Padding DefaultPadding => base.DefaultPadding;
    protected override Size DefaultSize => base.DefaultSize;
    protected override Boolean ScaleChildren => base.ScaleChildren;
    protected override Boolean ShowFocusCues => base.ShowFocusCues;
    protected override Boolean ShowKeyboardCues => base.ShowKeyboardCues;
    protected override Boolean ShowWithoutActivation => base.ShowWithoutActivation;

    public override Rectangle DisplayRectangle => base.DisplayRectangle;
    public override Boolean Focused => base.Focused;
    public HashSet<String> FoundFiles => _foundFiles;
    public Hasher Hasher => _hasher;
    public override LayoutEngine LayoutEngine => base.LayoutEngine;
    */
  }
}
