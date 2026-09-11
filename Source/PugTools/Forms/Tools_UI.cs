using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PugTools {
  internal partial class Tools {
    delegate void ClearlistCallback();
    delegate void ClearProgressCallback();
    delegate void ContinuousProgressCallback();
    delegate void ProgressCallback(Int32 progress, Int32 count);
    delegate void SetTextCallback(String text);
    delegate void SetText2Callback(String text);

    private static Boolean HasOpenDataBrowserWindows() {
      return Application.OpenForms.Cast<Form>().Any(form =>
        form != null
        && !form.IsDisposed
        && (form is global::PugTools.AssetBrowser
            || form is global::PugTools.AssetBrowserFileTable
            || form is global::PugTools.ModelBrowser
            || form is global::PugTools.NodeBrowser
            || form is global::PugTools.WorldBrowser
            || form is global::PugTools.ShaderBrowser
            || form is global::PugTools.DomBrowser));
    }

    private void InitializeShaderBrowserButton() {
      if (gbxTools == null) return;
      Button shaderButton = new Button {
        Name = "btnShaderBrowser",
        Text = "Shader Browser",
        Location = new System.Drawing.Point(7, 122),
        Margin = new Padding(4, 3, 4, 3),
        Size = new System.Drawing.Size(126, 27),
        TabIndex = 52,
        UseVisualStyleBackColor = true
      };
      shaderButton.Click += BtnShaderBrowser_Click;
      gbxTools.Controls.Add(shaderButton);
      gbxTools.Height += 34;
      if (gbxFQN != null) gbxFQN.Top += 34;
      if (progressBar1 != null) progressBar1.Top += 34;
      ClientSize = new System.Drawing.Size(ClientSize.Width, ClientSize.Height + 34);
    }

    private void BtnShaderBrowser_Click(Object sender, EventArgs e) {
      Form browser = new ShaderBrowser(txtAssetsPath.Text, chkAssetsUsePTS.Checked);
      browser.Show();
      browser.Focus();
    }

    private void InitializeDomBrowserButton() {
      if (gbxTools == null) return;
      Button domButton = new Button {
        Name = "btnDomBrowser",
        Text = "DOM Browser",
        Location = new System.Drawing.Point(139, 122),
        Margin = new Padding(4, 3, 4, 3),
        Size = new System.Drawing.Size(126, 27),
        TabIndex = 53,
        UseVisualStyleBackColor = true
      };
      toolTip1?.SetToolTip(domButton, "Browse client.gom classes, fields, enums, composition and schema references without a database.");
      domButton.Click += BtnDomBrowser_Click;
      gbxTools.Controls.Add(domButton);
    }

    private void BtnDomBrowser_Click(Object sender, EventArgs e) {
      Form browser = new DomBrowser(txtAssetsPath.Text, chkAssetsUsePTS.Checked);
      browser.Show();
      browser.Focus();
    }

    private void AddToList1(String text) {
      if (listBox1.InvokeRequired) {
        SetTextCallback d = new SetTextCallback(AddToList1);
        Invoke(d, new Object[] { text });
      } else {
        listBox1.Items.Add(text);
        listBox1.TopIndex = listBox1.Items.Count - 1;

      }
    }
    private void AddToList2(String text) {
      if (listBox2.InvokeRequired) {
        SetText2Callback d = new SetText2Callback(AddToList2);
        Invoke(d, new Object[] { text });
      } else {
        listBox2.Items.Add(text);
        listBox2.TopIndex = listBox2.Items.Count - 1;
      }
    }
    private void BtnAssetBrowser_Click(Object sender, EventArgs e) {
      if (chkBuildCompare.Checked && String.IsNullOrWhiteSpace(txtPrevAssetsPath.Text)) {
        MessageBox.Show(
          "Please select a Previous Game Path before opening the browser in compare mode.",
          "Compare Builds",
          MessageBoxButtons.OK,
          MessageBoxIcon.Warning
        );
        return;
      }

      Boolean usePTS = chkAssetsUsePTS.Checked;
      Form browser = new AssetBrowser(
        txtAssetsPath.Text,
        usePTS,
        txtPrevAssetsPath.Text,
        chkPrevAssetsUsePTS.Checked,
        chkBuildCompare.Checked
      );
      browser.Show();
      browser.Focus();
    }
    private void BtnAssetsPath_Click(Object sender, EventArgs e) {
      FolderBrowserDialog fbd = new FolderBrowserDialog {
        SelectedPath = txtAssetsPath.Text,
        Description = "Select the SWTOR game folder (not the Assets subfolder)."
      };

      if (fbd.ShowDialog() != DialogResult.OK) return;

      String selectedPath = TorArchive.Assets.NormalizeGamePath(fbd.SelectedPath);
      txtAssetsPath.Text = selectedPath.EndsWith("\\") ? selectedPath : selectedPath + "\\";
    }
    private void BtnCreateSql_Click(Object sender, EventArgs e) {
      SqlCreate();
    }
    private void CbxLanguage_Changed(Object sender, EventArgs e) {
      if (cbxLanguage.SelectedItem == null) return;
      string locale = cbxLanguage.SelectedItem.ToString();
      LocalizationResolver.ApplyRequested(locale);
      if (TorArchive.AssetHandler.Instance.CurrentLoaded)
        LocalizationResolver.Apply(TorArchive.AssetHandler.Instance.GetCurrentAssets(), locale);
      if (!String.Equals(Config.Language, locale, StringComparison.OrdinalIgnoreCase)) {
        Config.Language = locale;
        Config.Save();
      }
    }

    private void BtnExtract_Click(Object sender, EventArgs e) {
      DisableButtons();

      try {
        String selected = cbxExtractors.SelectedItem.ToString();

        if ((cbxExtractFormat.SelectedItem.ToString() == "SQL"
            || cbxExtractFormat.SelectedItem.ToString() == "JSON")
            && txtVersion.Text == "") {
          MessageBox.Show("A patch version number is required for SQL or JSON Output.");
          EnableButtons();
          return;
        }

        if (!System.IO.Directory.Exists(Config.ExtractPath + s_prefix))
          System.IO.Directory.CreateDirectory(Config.ExtractPath + s_prefix);

        ThreadStart thread = null;

        switch (selected) {
          case "(Everything)":
            thread = new ThreadStart(GetAll);
            break;
          case "Abilities":
            thread = () => GetObjects(
              "abl.",
              "Abilities"
            );
            break;
          case "Achievements":
            thread = () => GetObjects(
              "ach.",
              "Achievements"
            );
            break;
          case "Areas":
            thread = () => GetPrototypeObjects(
              "Areas",
              "mapAreasDataProto",
              "mapAreasDataObjectList"
            ); //new ThreadStart(getAreas);
            break;
          case "Cartel Market":
            thread = () => GetPrototypeObjects(
              "MtxStoreFronts",
              "mtxStorefrontInfoPrototype",
              "mtxStorefrontData"
            ); //new ThreadStart(getMtx);
            break;
          case "Codex":
            thread = () => GetObjects(
              "cdx.",
              "CodexEntries"
            );
            break;
          case "Collections":
            thread = () => GetPrototypeObjects(
              "Collections",
              "colCollectionItemsPrototype",
              "colCollectionItemsData"
            ); //new ThreadStart(getCollect);
            break;
          case "Companions":
            thread = () => GetPrototypeObjects(
              "Companions",
              "chrCompanionInfo_Prototype",
              "chrCompanionInfoData"
            ); //new ThreadStart(getCompanions);
            break;
          case "Conversations":
            thread = () => GetObjects(
              "cnv.",
              "Conversations"
            );
            break;
          case "Disciplines":
            thread = () => GetObjects(
              "dis.",
              "Disciplines"
            );
            break;
          //case "Filenames": t = new ThreadStart(getFilenames);
          //break;
          case "Icons":
            thread = new ThreadStart(GetIcons);
            break;
          case "Items":
            thread = () => GetObjects(
              "itm.",
              "Items"
            );
            break;
          case "Item Appearances":
            thread = () => GetObjects(
              "ipp.",
              "ItemAppearances"
            ); //t = new ThreadStart(getItemApps);
            break;
          case "NPCs":
            thread = () => GetObjects(
              "npc.",
              "Npcs"
            );
            break;
          case "Quests":
            thread = () => GetObjects(
              "qst.",
              "Quests"
            );
            break;
          case "Raw GOM":
            thread = new ThreadStart(GetRaw);
            break;
          case "String Tables":
            thread = new ThreadStart(GetStrings);
            break;
          case "GSF Ships":
            thread = () => GetPrototypeObjects(
              "Ships",
              "scFFShipsDataPrototype",
              "scFFShipsData"
            ); //new ThreadStart(getSpaceShip);
            break;
          case "Talents":
            thread = () => GetObjects(
              "tal.",
              "Talents"
            );
            break;
          case "Schematics":
            thread = () => GetObjects(
              "schem.",
              "Schematics"
            );
            break;
          case "Decorations":
            thread = () => GetObjects(
              "dec.",
              "Decorations"
            );
            break;
          case "Ability Effects":
            thread = () => GetObjects(
              "eff.",
              "Effects"
            );
            break;
          case "Strongholds":
            thread = () => GetObjects(
              "apt.",
              "Strongholds"
            );
            break;
          case "Conquests":
            thread = () => GetPrototypeObjects(
              "Conquests",
              "wevConquestInfosPrototype",
              "wevConquestTable"
            );
            break;
          case "Advanced Classes":
            thread = () => GetObjects(
              "class.pc.advanced",
              "AdvancedClasses"
            );
            break;
          case "Find New MTX Images":
            thread = new ThreadStart(FindNewMtxImages);
            break;
          case "Achievement Categories":
            thread = () => GetPrototypeObjects(
              "AchCategories",
              "achCategoriesTable_Prototype",
              "achCategoriesData"
            );
            break;
          case "Verify Hashes":
            thread = new ThreadStart(VerifyHashes);
            break;
          case "Tooltips":
            thread = new ThreadStart(GetTooltips);
            break;
          case "Set Bonuses":
            thread = () => GetPrototypeObjects(
              "SetBonuses",
              "itmSetBonusesPrototype",
              "itmSetBonuses"
            );
            break;
          case "Codex Category Totals":
            thread = () => GetPrototypeObjects(
              "CodexCategoryTotals",
              "cdxCategoryTotalsPrototype",
              "cdxFactionToClassToPlanetToTotalLookupList"
            );
            break;
          case "Schematic Variations":
            thread = () => GetPrototypeObjects(
              "SchematicVariations",
              "prfSchematicVariationsPrototype",
              "prfSchematicVariationMasterList"
            );
            break;
          case "Build Bnk ID Dict":
            thread = new ThreadStart(BuildBnkIdDict);
            break;
            //case "Dulfy": t = new ThreadStart(getDisciplineCalcData);
            //    break;
        }

        if (thread != null) {
          Thread oGetItems = new Thread(thread);
          oGetItems.Start();
        } else {
          EnableButtons();
        }
      }
      catch (Exception ex) {
        Debug.WriteLine(ex.Message);
      }
    }
    private void BtnExtractPath_Click(Object sender, EventArgs e) {
      FolderBrowserDialog fbd = new FolderBrowserDialog {
        SelectedPath = txtExtractPath.Text
      };

      _ = fbd.ShowDialog();

      if (fbd.SelectedPath.EndsWith("\\")) {
        txtExtractPath.Text = fbd.SelectedPath;
      } else {
        txtExtractPath.Text = fbd.SelectedPath + "\\";
      }
    }
    private void BtnFileCompare_Click(Object sender, EventArgs e) {
      LoadData();
      TorArchive.HashDictionaryInstance hashdata = TorArchive.HashDictionaryInstance.Instance;
      hashdata.Load();
      HashSet<String> current = GetFilenameHashset(_currentAssets);
      HashSet<String> previous = GetFilenameHashset(_previousAssets);
      List<String> sorted = current.Except(previous).ToList();
      sorted.Sort();
      WriteFile(String.Join(Environment.NewLine, sorted), "newFiles.txt", false);
      EnableButtons();
    }
    private void BtnModelBrowser_Click(Object sender, EventArgs e) {
      if (chkBuildCompare.Checked && String.IsNullOrWhiteSpace(txtPrevAssetsPath.Text)) {
        MessageBox.Show(
          "Please select a Previous Game Path before opening the browser in compare mode.",
          "Compare Builds",
          MessageBoxButtons.OK,
          MessageBoxIcon.Warning
        );
        return;
      }

      Boolean usePTS = chkAssetsUsePTS.Checked;
      Form browser = new ModelBrowser(
        txtAssetsPath.Text,
        usePTS,
        txtPrevAssetsPath.Text,
        chkPrevAssetsUsePTS.Checked,
        chkBuildCompare.Checked
      );
      browser.Show();
      browser.Focus();
    }
    private void BtnNodeBrowser_Click(Object sender, EventArgs e) {
      if (chkBuildCompare.Checked && String.IsNullOrWhiteSpace(txtPrevAssetsPath.Text)) {
        MessageBox.Show(
          "Please select a Previous Game Path before opening the browser in compare mode.",
          "Compare Builds",
          MessageBoxButtons.OK,
          MessageBoxIcon.Warning
        );
        return;
      }

      Boolean usePTS = chkAssetsUsePTS.Checked;
      Form browser = new NodeBrowser(
        txtAssetsPath.Text,
        usePTS,
        txtExtractPath.Text,
        txtPrevAssetsPath.Text,
        chkPrevAssetsUsePTS.Checked,
        chkBuildCompare.Checked
      );
      browser.Show();
      browser.Focus();
    }
    private void BtnPrevAssetsPath_Click(Object sender, EventArgs e) {
      FolderBrowserDialog fbd = new FolderBrowserDialog {
        SelectedPath = txtPrevAssetsPath.Text,
        Description = "Select the SWTOR game folder for the previous/compare build."
      };

      if (fbd.ShowDialog() != DialogResult.OK) return;

      String selectedPath = TorArchive.Assets.NormalizeGamePath(fbd.SelectedPath);
      txtPrevAssetsPath.Text = selectedPath.EndsWith("\\") ? selectedPath : selectedPath + "\\";
    }
    private void BtnSearch_Click(Object sender, EventArgs e) {
      DisableButtons();
      listBox1.Items.Add("Geting info for fqn " + tbxFqnSearch.Text);
      listBox1.Items.Add("Will output raw GOM for model building to:");
      listBox1.Items.Add(Config.ExtractPath + "GOM\\ in xml format");
      Thread oGetItems = new Thread(delegate () { GetFqn(tbxFqnSearch.Text); });
      oGetItems.Start();
    }
    private void BtnToggleSql_Click(Object sender, EventArgs e) {
      if (_sql) {
        listBox1.Items.Add("Mysql is now OFF");
        btnToggleSql.Text = "Mysql OFF";
        txtSqlUsername.Enabled = false;
        txtSqlAddress.Enabled = false;
        txtSqlName.Enabled = false;
        txtSqlPassword.Enabled = false;
        _sql = false;
      } else {
        listBox1.Items.Add("Mysql is now ON");
        btnToggleSql.Text = "Mysql ON";
        txtSqlUsername.Enabled = true;
        txtSqlAddress.Enabled = true;
        txtSqlName.Enabled = true;
        txtSqlPassword.Enabled = true;
        _sql = true;
      }
    }
    private async void BtnUnloadAllData_Click(Object sender, EventArgs e) {
      // Open browsers deliberately share Assets/DOM/hash caches. Disposing those process-wide
      // objects out from underneath a browser produces use-after-dispose/null races. Make an
      // explicit unload safe and predictable instead of allowing one window to kill the others.
      if (HasOpenDataBrowserWindows()) {
        MessageBox.Show(
          "Close the open Asset / Model / Node / World Browser windows before unloading shared data.",
          "Shared SWTOR data is in use",
          MessageBoxButtons.OK,
          MessageBoxIcon.Information
        );
        return;
      }

      DisableButtons();
      Clearlist();
      AddToList1("All Assets & DOM - Clearing");
      await Task.Run(() => UnloadAll());
      AddToList1("All Assets & DOM - Cleared");
      EnableButtons();
    }
    private void BtnWorldBrowser_Click(Object sender, EventArgs e) {
      Boolean usePTS = chkAssetsUsePTS.Checked;
      Form browser = new PugTools.WorldBrowser(txtAssetsPath.Text, usePTS);
      browser.Show();
      browser.Focus();
    }
    private void CbxExtractFormat_Changed(Object sender, EventArgs e) {
      s_outputTypeName = cbxExtractFormat.SelectedItem.ToString();
    }
    private void ChkBuildCompare_Changed(Object sender, EventArgs e) {
      if (chkBuildCompare.Checked && s_loaded && PreviousDom == null) {
        // Do not dispose the current shared DOM just because compare mode was enabled while
        // browser windows are open. Mark the Tools-side load state stale; the next LoadData()
        // reuses the already loaded current Assets/DOM and adds the previous build alongside it.
        s_loaded = false;
        AddToList1("Compare mode enabled; previous build will be loaded alongside current data.");
      }
    }
    private void ChkCrossLinkDom_Changed(Object sender, EventArgs e) {
      Config.CrossLinkDOM = chkCrossLinkDom.Checked;
      Config.Save();
    }
    private async void ChkPrevUsePTSAssets_Changed(Object sender, EventArgs e) {
      Boolean previousModeChanged = chkPrevAssetsUsePTS.Checked != Config.PrevAssetsUsePTS;
      if (previousModeChanged && HasOpenDataBrowserWindows()) {
        MessageBox.Show(
          "Close the open browser windows before changing the Previous Build LIVE/PTS source.",
          "Shared SWTOR data is in use",
          MessageBoxButtons.OK,
          MessageBoxIcon.Information
        );
        chkPrevAssetsUsePTS.Checked = Config.PrevAssetsUsePTS;
        return;
      }

      Config.PrevAssetsUsePTS = chkPrevAssetsUsePTS.Checked;
      Config.Save();

      String path = txtPrevAssetsPath.Text;

      if (chkPrevAssetsUsePTS.Checked) {
        if (PathContainsPTSAssets(path))
          btnPrevAssetsPath.Image = Properties.Resources.ShieldGreen;
        else
          btnPrevAssetsPath.Image = Properties.Resources.ShieldRed;
      } else {
        if (PathContainsLiveAssets(path))
          btnPrevAssetsPath.Image = Properties.Resources.ShieldGreen;
        else
          btnPrevAssetsPath.Image = Properties.Resources.ShieldRed;
      }

      if (s_loaded && previousModeChanged) {
        DisableButtons();
        Clearlist();
        AddToList1("Previous Assets & DOM - Clearing");
        await Task.Run(() => UnloadPrevious());
        AddToList1("Previous Assets & DOM - Cleared");
        EnableButtons();
        GC.Collect();
      }
    }
    private void ChkRemoveElements_Changed(Object sender, EventArgs e) {
      s_removeUnchanged = chkRemoveElements.CheckState == CheckState.Checked;
    }
    private async void ChkUsePTSAssets_Changed(Object sender, EventArgs e) {
      Boolean modeChanged = chkAssetsUsePTS.Checked != Config.AssetsUsePTS;
      if (modeChanged && HasOpenDataBrowserWindows()) {
        MessageBox.Show(
          "Close the open browser windows before changing the Current Build LIVE/PTS source.",
          "Shared SWTOR data is in use",
          MessageBoxButtons.OK,
          MessageBoxIcon.Information
        );
        chkAssetsUsePTS.Checked = Config.AssetsUsePTS;
        return;
      }

      Config.AssetsUsePTS = chkAssetsUsePTS.Checked;
      Config.Save();

      String path = txtAssetsPath.Text;

      if (chkAssetsUsePTS.Checked) {
        if (PathContainsPTSAssets(path))
          btnAssetsPath.Image = Properties.Resources.ShieldGreen;
        else
          btnAssetsPath.Image = Properties.Resources.ShieldRed;
      } else {
        if (PathContainsLiveAssets(path))
          btnAssetsPath.Image = Properties.Resources.ShieldGreen;
        else
          btnAssetsPath.Image = Properties.Resources.ShieldRed;
      }

      if (s_loaded && modeChanged) {
        DisableButtons();
        Clearlist();
        AddToList1("Current Assets & DOM - Clearing");
        await Task.Run(() => UnloadCurrent());
        AddToList1("Current Assets & DOM - Cleared");
        EnableButtons();
        GC.Collect();
      }
    }
    private void ChkVerbose_Changed(Object sender, EventArgs e) {
      Verbose = chkVerbose.CheckState == CheckState.Checked;
      //if (verbose) prefix = "Verbose";
      //else prefix = "";
    }
    private void Clearlist() {
      if (listBox1.InvokeRequired) {
        ClearlistCallback d = new ClearlistCallback(Clearlist);
        Invoke(d, Array.Empty<Object>());
      } else {
        listBox1.Items.Clear();
      }
    }
    private void Clearlist2() {
      if (listBox1.InvokeRequired) {
        ClearlistCallback d = new ClearlistCallback(Clearlist2);
        Invoke(d, Array.Empty<Object>());
      } else {
        listBox2.Items.Clear();
      }
    }
    private void ClearProgress() {
      if (progressBar1.InvokeRequired) {
        ClearProgressCallback d = new ClearProgressCallback(ClearProgress);
        Invoke(d, Array.Empty<Object>());
      } else {
        progressBar1.Style = ProgressBarStyle.Blocks;
        progressBar1.Value = 0;
      }
    }
    private void ContinuousProgress() {
      if (progressBar1.InvokeRequired) {
        ContinuousProgressCallback d = new ContinuousProgressCallback(ContinuousProgress);
        Invoke(d, Array.Empty<Object>());
      } else {
        progressBar1.Style = ProgressBarStyle.Marquee;
        progressBar1.MarqueeAnimationSpeed = 25;
      }
    }
    private void DisableButtons() {
      if (txtAssetsPath.InvokeRequired) Invoke((Action)DisableButtons);
      else {
        txtAssetsPath.Enabled = false;
        txtExtractPath.Enabled = false;
        txtPrevAssetsPath.Enabled = false;
        btnExtractPath.Enabled = false;
        btnAssetsPath.Enabled = false;
        btnPrevAssetsPath.Enabled = false;
        gbxFormat.Enabled = false;
        gbxSQL.Enabled = false;
        gbxExtract.Enabled = false;
        gbxTools.Enabled = false;
        gbxFQN.Enabled = false;
        gbxLogs.Enabled = false;

        ContinuousProgress();
      }
    }
    private void EnableButtons() {
      if (txtAssetsPath.InvokeRequired) Invoke((Action)EnableButtons);
      else {
        txtAssetsPath.Enabled = true;
        txtExtractPath.Enabled = true;
        txtPrevAssetsPath.Enabled = true;
        btnExtractPath.Enabled = true;
        btnAssetsPath.Enabled = true;
        btnPrevAssetsPath.Enabled = true;
        gbxFormat.Enabled = true;
        gbxSQL.Enabled = true;
        gbxExtract.Enabled = true;
        gbxTools.Enabled = true;
        gbxFQN.Enabled = true;
        gbxLogs.Enabled = true;

        ClearProgress();
      }
    }
    private static HashSet<String> GetFilenameHashset(TorArchive.Assets assets) {
      HashSet<String> ret = new HashSet<String>();

      foreach (TorArchive.Library lib in assets.Libraries) {
        String path = lib.Location;

        if (!lib.Loaded) lib.Load();

        if (lib.Archives.Count > 0) {
          foreach (KeyValuePair<Int32, TorArchive.Archive> arch in lib.Archives) {
            foreach (TorArchive.File file in arch.Value.EnumerateFiles()) {
              TorArchive.HashFileInfo hashInfo =
                new TorArchive.HashFileInfo(
                  file.FileInfo.PrimaryHash,
                  file.FileInfo.SecondaryHash,
                  file
                );

              if (hashInfo.IsNamed) {
                if (hashInfo.FileName == "metadata.bin"
                    || hashInfo.FileName == "ft.sig"
                    || hashInfo.FileName == "groupmanifest.bin") {
                  continue;
                }

                ret.Add(hashInfo.Directory + "/" + hashInfo.FileName);
              } else {
                ret.Add(hashInfo.Directory
                + "/"
                + hashInfo.Extension
                + "/"
                + hashInfo.FileName
                + "."
                + hashInfo.Extension);
              }
            }
          }
        }
      }

      return ret;
    }
    private void ProgressUpdate(Int32 progress, Int32 count) {
      if (progressBar1.InvokeRequired) {
        ProgressCallback d = new ProgressCallback(ProgressUpdate);
        Invoke(d, new Object[] { progress, count });
      } else {
        Int32 value = 0;
        if (count != 0)
          value = (progress * 100) / count;
        progressBar1.Value = value;
        progressBar1.Update();
      }
    }
    private async void TxtAssetsPath_Changed(Object sender, EventArgs e) {
      String path = txtAssetsPath.Text;

      if (path.Length > 0 && !path.EndsWith("\\")) path += "\\";

      String configuredPath = Config.AssetsPath ?? String.Empty;
      Boolean pathChanged = !String.Equals(path, configuredPath, StringComparison.OrdinalIgnoreCase);
      if (pathChanged && HasOpenDataBrowserWindows()) {
        MessageBox.Show(
          "Close the open browser windows before changing the Current Game Path.",
          "Shared SWTOR data is in use",
          MessageBoxButtons.OK,
          MessageBoxIcon.Information
        );
        txtAssetsPath.Text = configuredPath;
        return;
      }

      Config.AssetsPath = path;
      Config.Save();

      Boolean hasLive = PathContainsLiveAssets(path);
      Boolean hasPTS = PathContainsPTSAssets(path);

      if (hasLive && !hasPTS) {
        btnAssetsPath.Image = Properties.Resources.ShieldGreen;
        chkAssetsUsePTS.Checked = false;
      } else if (!hasLive && hasPTS) {
        btnAssetsPath.Image = Properties.Resources.ShieldGreen;
        chkAssetsUsePTS.Checked = true;
      } else if (!hasLive && !hasPTS) {
        btnAssetsPath.Image = Properties.Resources.ShieldRed;
      } else {
        //Has both live and pts assets.
        btnAssetsPath.Image = Properties.Resources.ShieldGreen;
      }

      if (s_loaded && pathChanged) {
        DisableButtons();
        Clearlist();
        AddToList1("Current Assets & DOM - Clearing");

        await Task.Run(() => UnloadCurrent());

        AddToList1("Current Assets & DOM - Cleared");
        EnableButtons();
        //Unload();
      }
    }
    private void TxtExtractPath_Changed(Object sender, EventArgs e) {
      String path = txtExtractPath.Text;

      if (path.Length > 0 && !path.EndsWith("\\")) path += "\\";

      Config.ExtractPath = path;
      Config.Save();
    }
    private async void TxtPrevAssetsPath_Changed(Object sender, EventArgs e) {
      String path = txtPrevAssetsPath.Text;

      if (path.Length > 0 && !path.EndsWith("\\")) path += "\\";

      String configuredPreviousPath = Config.PrevAssetsPath ?? String.Empty;
      Boolean previousPathChanged = !String.Equals(path, configuredPreviousPath, StringComparison.OrdinalIgnoreCase);
      if (previousPathChanged && HasOpenDataBrowserWindows()) {
        MessageBox.Show(
          "Close the open browser windows before changing the Previous Game Path.",
          "Shared SWTOR data is in use",
          MessageBoxButtons.OK,
          MessageBoxIcon.Information
        );
        txtPrevAssetsPath.Text = configuredPreviousPath;
        return;
      }

      Boolean hasLive = PathContainsLiveAssets(path);
      Boolean hasPTS = PathContainsPTSAssets(path);

      if (hasLive && !hasPTS) {
        btnPrevAssetsPath.Image = Properties.Resources.ShieldGreen;
        chkPrevAssetsUsePTS.Checked = false;
      } else if (!hasLive && hasPTS) {
        btnPrevAssetsPath.Image = Properties.Resources.ShieldGreen;
        chkPrevAssetsUsePTS.Checked = true;
      } else if (!hasLive && !hasPTS) {
        btnPrevAssetsPath.Image = Properties.Resources.ShieldRed;
      } else {
        //Has both live and pts assets.
        btnPrevAssetsPath.Image = Properties.Resources.ShieldGreen;
      }

      Config.PrevAssetsPath = path;
      Config.Save();

      if (s_loaded && previousPathChanged) {
        DisableButtons();
        Clearlist();
        AddToList1("Previous Assets & DOM - Clearing");

        await Task.Run(() => UnloadPrevious());

        AddToList1("Previous Assets & DOM - Cleared");
        EnableButtons();
        GC.Collect();
        //Unload();
      }
    }
    private void TxtVersion_Changed(Object sender, EventArgs e) {
      PatchVersion = txtVersion.Text;
    }
  }
}
