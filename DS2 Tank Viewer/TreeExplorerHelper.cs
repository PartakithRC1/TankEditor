public static class TreeExplorerHelper
{
    public static void PopulateTreeView(TreeView treeView, List<string> filePaths)
    {
        treeView.BeginUpdate();
        treeView.Nodes.Clear();

        foreach (string path in filePaths)
        {
            // Assuming your file paths use '/' as separators. 
            // If they use '\', change the split character.
            string[] pathParts = path.Split('/');
            TreeNodeCollection nodes = treeView.Nodes;

            foreach (string part in pathParts)
            {
                // Try to find the node if it already exists
                TreeNode foundNode = null;
                foreach (TreeNode node in nodes)
                {
                    if (node.Text == part)
                    {
                        foundNode = node;
                        break;
                    }
                }

                // If not found, create it
                if (foundNode == null)
                {
                    foundNode = new TreeNode(part);
                    // Optional: Assign images if you have an ImageList set up
                    // foundNode.ImageIndex = 0; // Folder
                    // foundNode.SelectedImageIndex = 0;
                    nodes.Add(foundNode);
                }

                // Move deeper into the tree
                nodes = foundNode.Nodes;
            }
        }

        treeView.EndUpdate();
    }

}