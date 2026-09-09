using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using PugTools;

namespace TreeViewFast.Controls {

  public class TreeViewFast : TreeView {
    #region Fields
    private Dictionary<String, TreeNode> _treeNodes = new Dictionary<String, TreeNode>();
    #endregion

    #region Properties
    #endregion

    /// <summary>
    /// A completely prepared managed tree. TreeNode instances are created and linked without
    /// touching the WinForms control, so large trees can be prepared on a worker thread and
    /// attached to the native TreeView only on the UI thread. The same prepared tree can be
    /// re-attached after a live filter without sorting and allocating every TreeNode again.
    /// </summary>
    internal sealed class PreparedTree {
      internal Dictionary<String, TreeNode> NodeMap { get; }
      internal TreeNode[] RootNodes { get; }

      internal PreparedTree(Dictionary<String, TreeNode> nodeMap, TreeNode[] rootNodes) {
        NodeMap = nodeMap ?? throw new ArgumentNullException(nameof(nodeMap));
        RootNodes = rootNodes ?? Array.Empty<TreeNode>();
      }
    }

    #region  Methods
    internal static PreparedTree PrepareItems<T>(
      IEnumerable<T> items,
      Func<T, String> getId,
      Func<T, String> getParentId,
      Func<T, String> getDisplayName,
      Func<T, Int32> getImageIndex = null,
      Comparison<T> comparison = null,
      Func<Boolean> shouldCancel = null
    ) {
      if (items == null) throw new ArgumentNullException(nameof(items));
      if (getId == null) throw new ArgumentNullException(nameof(getId));
      if (getParentId == null) throw new ArgumentNullException(nameof(getParentId));
      if (getDisplayName == null) throw new ArgumentNullException(nameof(getDisplayName));

      List<T> sortedItems = new List<T>();
      foreach (T item in items) {
        if (shouldCancel?.Invoke() == true) throw new OperationCanceledException();
        sortedItems.Add(item);
      }

      Int32 compareCounter = 0;
      sortedItems.Sort((x, y) => {
        // Sorting a several-hundred-thousand-entry browser tree can otherwise
        // continue burning a core long after the window was closed.
        if (((++compareCounter) & 0x3FF) == 0 && shouldCancel?.Invoke() == true)
          throw new OperationCanceledException();
        return comparison != null
          ? comparison(x, y)
          : String.Compare(getId(x), getId(y), StringComparison.Ordinal);
      });

      var nodeMap = new Dictionary<String, TreeNode>(sortedItems.Count, StringComparer.Ordinal);
      Int32 buildCounter = 0;
      foreach (T item in sortedItems) {
        if (((++buildCounter) & 0xFF) == 0 && shouldCancel?.Invoke() == true)
          throw new OperationCanceledException();
        String id = getId(item);
        TreeNode node = new TreeNode {
          Name = id,
          Text = getDisplayName(item),
          Tag = item
        };

        if (getImageIndex != null) {
          Int32 imageIndex = getImageIndex(item);
          node.ImageIndex = imageIndex;
          node.SelectedImageIndex = imageIndex;
        }

        nodeMap.Add(id, node);
      }

      var roots = new List<TreeNode>();
      buildCounter = 0;
      foreach (T item in sortedItems) {
        if (((++buildCounter) & 0xFF) == 0 && shouldCancel?.Invoke() == true)
          throw new OperationCanceledException();
        String id = getId(item);
        TreeNode node = nodeMap[id];
        String parentId = getParentId(item);

        if (!String.IsNullOrEmpty(parentId)) {
          if (nodeMap.TryGetValue(parentId, out TreeNode parentNode))
            parentNode.Nodes.Add(node);
          else
            roots.Add(node);
        } else {
          roots.Add(node);
        }
      }

      return new PreparedTree(nodeMap, roots.ToArray());
    }

    internal void LoadPrepared(PreparedTree preparedTree) {
      if (preparedTree == null) throw new ArgumentNullException(nameof(preparedTree));

      Nodes.Clear();
      _treeNodes = preparedTree.NodeMap;
      if (preparedTree.RootNodes.Length > 0) Nodes.AddRange(preparedTree.RootNodes);
    }

    /// <summary>
    /// Load the TreeView with items.
    /// </summary>
    /// <typeparam name="T">Item type</typeparam>
    /// <param name="items">Collection of items</param>
    /// <param name="getId">Function to parse Id value from item object</param>
    /// <param name="getParentId">Function to parse parentId value from item object</param>
    /// <param name="getDisplayName">Function to parse display name value from item object. 
    /// This is used as node text.</param>
    public void LoadItems<T>(IEnumerable<T> items,
                             Func<T, String> getId,
                             Func<T, String> getParentId,
                             Func<T, String> getDisplayName) {

      // Clear view and internal dictionary
      Nodes.Clear();
      // Replace rather than clear: a prepared full-tree snapshot may still own the previous map.
      _treeNodes = new Dictionary<String, TreeNode>();

      // Load internal dictionary with nodes
      foreach (T item in items) {
        String id = getId(item);
        String displayName = getDisplayName(item);
        TreeNode node = new TreeNode {
          Name = id.ToString(),
          Text = displayName,
          Tag = item
        };
        _treeNodes.Add(getId(item), node);
      }

      // Create hierarchy and load into view
      foreach (String id in _treeNodes.Keys) {
        TreeNode node = GetNode(id);
        T obj = (T)node.Tag;
        String parentId = getParentId(obj);

        if (parentId != "") {
          TreeNode parentNode = GetNode(parentId);
          parentNode.Nodes.Add(node);
        } else {
          Nodes.Add(node);
        }
      }
    }

    /// <summary>
    /// Get a handle to the object collection.
    /// This is convenient if you want to search the object collection.
    /// </summary>
    public IQueryable<T> GetItems<T>() {
      return _treeNodes.Values.Select(x => (T)x.Tag).AsQueryable();
    }

    /// <summary>
    /// Retrieve TreeNode by Id.
    /// Useful when you want to select a specific node.
    /// </summary>
    /// <param name="id">Item id</param>
    public TreeNode GetNode(String id) {
      return _treeNodes[id];
    }

    /// <summary>
    /// Retrieve item object by Id.
    /// Useful when you want to get hold of object for reading or further manipulating.
    /// </summary>
    /// <typeparam name="T">Item type</typeparam>
    /// <param name="id">Item id</param>
    /// <returns>Item object</returns>
    public T GetItem<T>(String id) {
      return (T)GetNode(id).Tag;
    }


    /// <summary>
    /// Get parent item.
    /// Will return NULL if item is at top level.
    /// </summary>
    /// <typeparam name="T">Item type</typeparam>
    /// <param name="id">Item id</param>
    /// <returns>Item object</returns>
    public T GetParent<T>(String id) where T : class {
      TreeNode parentNode = GetNode(id).Parent;
      return parentNode == null ? null : (T)parentNode.Tag;
    }

    /// <summary>
    /// Retrieve descendants to specified item.
    /// </summary>
    /// <typeparam name="T">Item type</typeparam>
    /// <param name="id">Item id</param>
    /// <param name="deepLimit">Number of generations to traverse down. 1 means only direct 
    /// children. Null means no limit.</param>
    /// <returns>List of item objects</returns>
    public List<T> GetDescendants<T>(String id, Int32? deepLimit = null) {
      TreeNode node = GetNode(id);
      IEnumerator enumerator = node.Nodes.GetEnumerator();
      List<T> items = new List<T>();

      if (deepLimit.HasValue && deepLimit.Value <= 0)
        return items;

      while (enumerator.MoveNext()) {
        // Add child
        TreeNode childNode = (TreeNode)enumerator.Current;
        T childItem = (T)childNode.Tag;
        items.Add(childItem);

        // If requested add grandchildren recursively
        Int32? childDeepLimit = deepLimit.HasValue ? deepLimit.Value - 1 : null;

        if (!deepLimit.HasValue || childDeepLimit > 0) {
          String childId = childNode.Name.ToString();
          List<T> descendants = GetDescendants<T>(childId, childDeepLimit);
          items.AddRange(descendants);
        }
      }
      return items;
    }

    internal /*async*/ void LoadItems<T1>(Dictionary<String, TreeListItem> testDict,
                                Func<TreeListItem, String> getId,
                                Func<TreeListItem, String> getParentId,
                                Func<TreeListItem, String> getDisplayName) {
      // Clear view and internal dictionary
      Nodes.Clear();
      // Replace rather than clear: a prepared full-tree snapshot may still own the previous map.
      _treeNodes = new Dictionary<String, TreeNode>();

      List<String> keys = testDict.Keys.ToList();
      keys.Sort(delegate (String x, String y) {
        TreeListItem tx = testDict[x];
        TreeListItem ty = testDict[y];

        if (tx.HashInfo.File == null && ty.HashInfo.File != null)
          return -1;
        else if (tx.HashInfo.File != null && ty.HashInfo.File == null)
          return 1;
        else if (tx.HashInfo.File == null && ty.HashInfo.File == null)
          return String.Compare(x, y);
        else
          return String.Compare(x, y);
      });

      // Load internal dictionary with nodes
      // _treeNodes = await Task.Run(async delegate {
      //   Dictionary<String, TreeNode> treeNodes = new Dictionary<String, TreeNode>();

      //   foreach (String key in keys) {
      //     TreeListItem item = testDict[key];
      //     String id = await Task.Run(() => getId(item));
      //     String displayName = await Task.Run(() => getDisplayName(item));
      //     TreeNode node = new TreeNode {
      //       Name = id.ToString(),
      //       Text = displayName,
      //       Tag = item
      //     };

      //     if (item.HashInfo.File != null) {
      //       node.ImageIndex = 2;
      //       node.SelectedImageIndex = 2;
      //     } else {
      //       node.ImageIndex = 1;
      //       node.SelectedImageIndex = 1;
      //     }

      //     treeNodes.Add(id, node);
      //   }

      //   return treeNodes;
      // });

      // Load internal dictionary with nodes
      foreach (String key in keys) {
        TreeListItem item = testDict[key];
        String id = getId(item);
        String displayName = getDisplayName(item);
        TreeNode node = new TreeNode {
          Name = id.ToString(),
          Text = displayName,
          Tag = item
        };

        if (item.HashInfo.File != null) {
          node.ImageIndex = 2;
          node.SelectedImageIndex = 2;
        } else {
          node.ImageIndex = 1;
          node.SelectedImageIndex = 1;
        }

        _treeNodes.Add(getId(item), node);
      }

      // TreeNode[] nodeArr = await Task.Run(async delegate {
      //   List<TreeNode> nodes = new List<TreeNode>();

      //   foreach (String id in _treeNodes.Keys) {
      //     TreeNode node = await Task.Run(() => GetNode(id));
      //     TreeListItem obj = node.Tag as TreeListItem;
      //     String parentId = await Task.Run(() => getParentId(obj));

      //     if (!String.IsNullOrEmpty(parentId)) {
      //       TreeNode parentNode = await Task.Run(() => GetNode(parentId));
      //       parentNode.Nodes.Add(node);
      //     } else {
      //       nodes.Add(node);
      //     }
      //   }
      //   return nodes.ToArray();
      // });

      // Invoke(new Action(() => Nodes.AddRange(nodeArr)));

      // Create hierarchy and load into view
      foreach (String id in _treeNodes.Keys) {
        TreeNode node = GetNode(id);
        TreeListItem obj = (TreeListItem)node.Tag;
        String parentId = getParentId(obj);

        if (parentId != "") {
          TreeNode parentNode = GetNode(parentId);
          parentNode.Nodes.Add(node);
        } else {
          Nodes.Add(node);
        }
      }
    }

    internal void LoadItems<T1>(Dictionary<String, TreeListItem> assetDict,
                                Func<TreeListItem, String> getId,
                                Func<TreeListItem, String> getParentId,
                                Func<TreeListItem, String> getDisplayName,
                                String filter,
                                String type) {

      if (filter == null) throw new ArgumentNullException(nameof(filter));
      if (type == null) throw new ArgumentNullException(nameof(type));

      // Clear view and internal dictionary
      Nodes.Clear();
      // Replace rather than clear: a prepared full-tree snapshot may still own the previous map.
      _treeNodes = new Dictionary<String, TreeNode>();

      List<String> keys = assetDict.Keys.ToList();
      keys.Sort();

      // Load internal dictionary with nodes
      foreach (String key in keys) {
        TreeListItem item = assetDict[key];
        String id = getId(item);
        String displayName = getDisplayName(item);
        TreeNode node = new TreeNode {
          Name = id.ToString(),
          Text = displayName,
          Tag = item
        };

        if (item.HashInfo.File != null) {
          node.ImageIndex = 2;
          node.SelectedImageIndex = 2;
        } else {
          node.ImageIndex = 1;
          node.SelectedImageIndex = 1;
        }

        _treeNodes.Add(getId(item), node);
      }

      // Create hierarchy and load into view
      foreach (String id in _treeNodes.Keys) {
        TreeNode node = GetNode(id);
        TreeListItem obj = (TreeListItem)node.Tag;
        String parentId = getParentId(obj);

        if (parentId != "") {
          TreeNode parentNode = GetNode(parentId);
          parentNode.Nodes.Add(node);
        } else {
          Nodes.Add(node);
        }
      }
    }

    internal void LoadItems<T1>(Dictionary<String, NodeAsset> assetDict,
                                Func<NodeAsset, String> getId,
                                Func<NodeAsset, String> getParentId,
                                Func<NodeAsset, String> getDisplayName) {
      // Clear view and internal dictionary
      Nodes.Clear();
      // Replace rather than clear: a prepared full-tree snapshot may still own the previous map.
      _treeNodes = new Dictionary<String, TreeNode>();

      List<String> keys = assetDict.Keys.ToList();
      keys.Sort(delegate (String x, String y) {
        NodeAsset tx = assetDict[x];
        NodeAsset ty = assetDict[y];

        if (tx.Obj == null
            && tx.dynObject == null
            && tx.objData == null
            && (ty.Obj != null || ty.dynObject != null || ty.objData != null))
          return -1;
        else if ((tx.Obj != null
                  || tx.dynObject != null
                  || tx.objData != null)
                  && ty.Obj == null && ty.dynObject == null && ty.objData == null)
          return 1;
        else if (tx.Obj == null
                 && tx.dynObject == null
                 && tx.objData == null
                 && ty.Obj == null
                 && ty.dynObject == null
                 && ty.objData == null)
          return String.Compare(x, y);
        else
          return String.Compare(tx.id, ty.id);
      });

      // Load internal dictionary with nodes
      foreach (String key in keys) {
        NodeAsset item = assetDict[key];
        String id = getId(item);
        String displayName = getDisplayName(item);
        TreeNode node = new TreeNode {
          Name = id.ToString(),
          Text = displayName,
          Tag = item
        };

        if (item.Obj != null || item.dynObject != null || item.objData != null) {
          node.ImageIndex = 2;
          node.SelectedImageIndex = 2;
        } else {
          node.ImageIndex = 1;
          node.SelectedImageIndex = 1;
        }

        _treeNodes.Add(getId(item), node);
      }

      // Create hierarchy and load into view
      foreach (String id in _treeNodes.Keys) {
        TreeNode node = GetNode(id);
        NodeAsset obj = (NodeAsset)node.Tag;
        String parentId = getParentId(obj);

        if (parentId != "") {
          TreeNode parentNode = GetNode(parentId);
          parentNode.Nodes.Add(node);
        } else {
          Nodes.Add(node);
        }
      }
    }

    internal void LoadItems<T1>(Dictionary<String, ArchTreeListItem> testDict,
                                Func<ArchTreeListItem, String> getId,
                                Func<ArchTreeListItem, String> getParentId,
                                Func<ArchTreeListItem, String> getDisplayName) {

      // Clear view and internal dictionary
      Nodes.Clear();
      // Replace rather than clear: a prepared full-tree snapshot may still own the previous map.
      _treeNodes = new Dictionary<String, TreeNode>();

      List<String> keys = testDict.Keys.ToList();
      keys.Sort(delegate (String x, String y) {
        ArchTreeListItem tx = testDict[x];
        ArchTreeListItem ty = testDict[y];

        return String.Compare(x, y);
      });

      // Load internal dictionary with nodes
      foreach (String key in keys) {
        ArchTreeListItem item = testDict[key];
        String id = getId(item);
        String displayName = getDisplayName(item);
        TreeNode node = new TreeNode {
          Name = id.ToString(),
          Text = displayName,
          Tag = item
        };
        node.ImageIndex = 1;
        node.SelectedImageIndex = 1;
        _treeNodes.Add(getId(item), node);
      }

      // Create hierarchy and load into view
      foreach (String id in _treeNodes.Keys) {
        TreeNode node = GetNode(id);
        ArchTreeListItem obj = (ArchTreeListItem)node.Tag;
        String parentId = getParentId(obj);

        if (parentId != "") {
          TreeNode parentNode = GetNode(parentId);
          parentNode.Nodes.Add(node);
        } else {
          Nodes.Add(node);
        }
      }
    }
    #endregion
  }
}
