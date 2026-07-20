using System.Windows.Forms;

namespace AnimeStudio.GUI
{
    public class GameObjectTreeNode : TreeNode
    {
        public GameObject gameObject;

        public GameObjectTreeNode(GameObject gameObject)
        {
            this.gameObject = gameObject;
            Text = gameObject.m_Name;
            if (gameObject.HasModel())
            {
                BackColor = System.Drawing.Color.LightBlue;
            }
        }
    }
}
