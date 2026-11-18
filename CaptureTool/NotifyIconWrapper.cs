using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace CaptureTool
{
    public partial class NotifyIconWrapper : Component
    {
        private Window owner;
        public NotifyIconWrapper(Window owner)
        {
            InitializeComponent();
            this.owner = owner;
            toolStripMenuItem_Open.Click += ToolStripMenuItem_Open_Click;
            toolStripMenuItem_Exit.Click += ToolStripMenuItem_Exit_Click;
            notifyIcon1.DoubleClick += NotifyIcon1_DoubleClick;
        }

        private void NotifyIcon1_DoubleClick(object sender, EventArgs e)
        {
            ViewOwner();
        }

        public NotifyIconWrapper(IContainer container)
        {
            container.Add(this);

            InitializeComponent();
        }

        private void ToolStripMenuItem_Exit_Click(object sender, EventArgs e)
        {
            // MainWindow の Closing を通し、保存・キャプチャの完了を待つ。
            owner.Dispatcher.BeginInvoke(new Action(() => owner.Close()));
        }

        private void ToolStripMenuItem_Open_Click(object sender, EventArgs e)
        {
            ViewOwner();
        }

        public IntPtr GetOwnerHandle()
        {
            var helper = new System.Windows.Interop.WindowInteropHelper(owner);
            return helper.Handle;
        }

        private void ViewOwner()
        {
            if (owner != null)
            {
                owner.Visibility = Visibility.Visible;
                owner.WindowState = WindowState.Normal;
                MainProcess.SwitchToThisWindow(GetOwnerHandle(), true);
            }
        }
    }
}
