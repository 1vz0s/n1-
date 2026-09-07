using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.IO;

namespace KeyMacro
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [DllImport("winmm.dll")]
        static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        static extern uint timeEndPeriod(uint uPeriod);

        [STAThread]
        static void Main()
        {
            try { SetProcessDPIAware(); } catch { }
            timeBeginPeriod(1); // 定时器精度提到 1ms，连点间隔才精准不抖
            try
            {
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs ex)
                {
                    try { System.IO.File.WriteAllText("crash.log", ex.Exception.ToString()); } catch { }
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs ex)
                {
                    try { System.IO.File.WriteAllText("crash.log", ex.ExceptionObject.ToString()); } catch { }
                };
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MacroForm());
            }
            finally
            {
                timeEndPeriod(1);
            }
        }
    }

    // 一个连点项：键盘键 或 鼠标键，各自独立、各自频率
    class Bind
    {
        public int type;            // 0=键盘 1=鼠标
        public int code;            // 键盘=Keys 值；鼠标=0左 1右 2中
        public string label;
        public volatile int freq = 20;  // 次/秒
        public volatile bool stop = false;
        public int trigger = (int)Keys.F6;   // 该键自己的触发键
        public volatile bool hold = true;    // true=长按连点 false=切换模式
        public volatile bool toggle = false; // 切换模式下的开/关
        public volatile bool clicking = false; // 当前是否在连点
        public Thread thread;
        public Random rnd = new Random(Guid.NewGuid().GetHashCode()); // 每个键独立随机源，人类化抖动用
    }

    class MacroEvent
    {
        public int delay;   // 距上一事件毫秒
        public int type;    // 0=键盘 1=鼠标
        public int action;  // 键盘0=down 1=up；鼠标0=move 1=左down 2=左up 3=右down 4=右up 5=中down 6=中up 7=滚轮
        public int code;    // 键盘=vk；鼠标=按钮
        public int x, y;    // 鼠标坐标
        public int wheel;   // 滚轮增量
    }

    public class MacroForm : Form
    {
        // 软件版本号 —— 每次修改软件都要递增这里
        const string APP_VERSION = "v1.3.0";

        // ================= Win32 P/Invoke =================
        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
        static extern IntPtr SetWindowsHookExKB(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint type;
            public INPUTUNION U;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct INPUTUNION
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public MOUSEINPUT mi;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT
        {
            public int pt_x;
            public int pt_y;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        const uint INPUT_MOUSE = 0;
        const uint INPUT_KEYBOARD = 1;
        const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        const uint KEYEVENTF_KEYUP = 0x0002;
        const uint KEYEVENTF_SCANCODE = 0x0008;

        const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        const uint MOUSEEVENTF_LEFTUP = 0x0004;
        const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

        const int WH_MOUSE_LL = 14;
        const int WM_LBUTTONDOWN = 0x0201;
        const int WM_RBUTTONDOWN = 0x0204;
        const int WM_MBUTTONDOWN = 0x0207;

        const int VK_LBUTTON = 0x01;
        const int VK_RBUTTON = 0x02;
        const int VK_MBUTTON = 0x04;

        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;
        const int LLKHF_INJECTED = 0x10;
        const int LLMHF_INJECTED = 0x01;

        const int WM_MOUSEMOVE = 0x0200;
        const int WM_MOUSEWHEEL = 0x020A;
        const int WM_LBUTTONUP = 0x0202;
        const int WM_RBUTTONUP = 0x0205;
        const int WM_MBUTTONUP = 0x0208;
        const int WM_KEYUP = 0x0101;
        const int WM_SYSKEYUP = 0x0105;

        const uint MOUSEEVENTF_MOVE = 0x0001;
        const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        const uint MOUSEEVENTF_WHEEL = 0x0800;
        const uint MOUSEEVENTF_VIRTUALDESK = 0x4000; // 绝对坐标映射整个虚拟桌面（多屏修正）

        const int PRESS_MS = 15; // 键盘按下到抬起的间隔（毫秒）
        const int MOUSE_CLICK_GAP = 2; // 鼠标点击 down/up 间隔（毫秒）——瞬点，别搞长按，长按会让游戏内光标闪、鼠标发钝降 DPI

        // ================= UI 控件 =================
        ListBox listKeys;
        TextBox txtTrigger;
        Button btnAddKey, btnDel, btnClear, btnPickTrigger;
        NumericUpDown numFreq;
        CheckBox chkHold;
        Label lblFreq, lblStatus;

        // ================= 状态 =================
        List<Bind> binds = new List<Bind>();

        int captureMode = 0; // 0=无 1=添加按键 2=设置触发键
        bool loading = false; // 加载配置中（避免加载时触发保存）

        volatile bool stopWatcher = false; // 停止所有后台线程
        readonly object sendLock = new object();

        LowLevelMouseProc mouseProc;
        IntPtr mouseHook;
        LowLevelKeyboardProc keyboardProc;
        IntPtr keyboardHook;
        System.Windows.Forms.Timer statusTimer;
        NotifyIcon trayIcon;       // 系统托盘图标（右下角小三角）
        ContextMenuStrip trayMenu; // 托盘右键菜单
        bool reallyExit = false;   // true=真正退出，false=X 只隐藏到托盘

        // ================= 选项卡 =================
        TabControl tabs;
        TabPage tabClicker, tabMacro;
        Control host; // 当前构建容器

        // ================= 宏录制 =================
        List<MacroEvent> macroEvents = new List<MacroEvent>();
        volatile bool recording = false;
        volatile bool playing = false;
        int lastTick = 0;

        Label lblMacroStatus;
        Button btnRec, btnPlay, btnClearMacro;
        ListBox listLog;         // 宏日志
        ComboBox cmbProcess;     // 录制目标进程下拉
        Button btnRefreshProc;   // 刷新进程
        int selectedProcId = 0;  // 选中进程 PID（0=不限制）
        CheckBox chkFilterProc;  // 进程过滤开关（勾选=只录/放指定进程）
        volatile bool filterEnabled = false; // 过滤开关状态（volatile 供回放线程读）
        volatile bool manualInterrupt = false; // 回放人工干预标志
        volatile bool sendingInput = false; // 回放/连点器发送 DD 键中（供钩子忽略）
        int lastMoveTick = 0;    // 鼠标移动采样时间戳

        TextBox txtMacroName;    // 宏名字输入
        ComboBox cmbMacroFile;   // 已保存宏列表
        Button btnSaveMacro, btnLoadMacro, btnRenameMacro, btnDelMacro;

        // 窗口缩放：记录初始布局，OnResize 里按比例缩放字体和控件
        Size initClient;
        float initFontSz;
        Dictionary<Control, Rectangle> initBounds = new Dictionary<Control, Rectangle>();
        bool layoutCaptured = false;
        Font scaledFont = null;

        public MacroForm()
        {
            this.Text = "n1按键宏 " + APP_VERSION;
            this.ClientSize = new Size(520, 560);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.KeyPreview = true;
            this.Font = new Font("Microsoft YaHei UI", 9F);
            try { this.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath); } catch { }

            BuildUi();
            if (!LoadConfig())
            {
                AddDefaultBinds();
            }
            UpdateStatus();
            CaptureInitLayout();
        }

        void AddDefaultBinds()
        {
            AddBind(0, (int)Keys.ControlKey, 20, (int)Keys.F6, true); // Ctrl
            AddBind(0, (int)Keys.F, 20, (int)Keys.F6, true);          // F
            AddBind(1, 0, 20, (int)Keys.F6, true);                    // 鼠标左键
        }

        // ================= 构建界面 =================
        void BuildUi()
        {
            tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            this.Controls.Add(tabs);

            tabClicker = new TabPage("连点");
            tabMacro = new TabPage("宏录制");
            tabs.TabPages.Add(tabClicker);
            tabs.TabPages.Add(tabMacro);

            host = tabClicker;
            AddLabel("连点按键列表（选中后调触发键/速度）", 20, 10, 480);

            listKeys = new ListBox();
            listKeys.Location = new Point(20, 34);
            listKeys.Size = new Size(360, 250);
            listKeys.IntegralHeight = false;
            listKeys.SelectedIndexChanged += delegate { OnListSelectedChanged(); };
            host.Controls.Add(listKeys);

            btnAddKey = AddButton("添加按键", 20, 300, 110, 34);
            btnAddKey.Click += delegate { StartCapture(1); };

            btnDel = AddButton("删除选中", 140, 300, 110, 34);
            btnDel.Click += delegate { DeleteSelected(); };

            btnClear = AddButton("清空", 260, 300, 120, 34);
            btnClear.Click += delegate { ClearBinds(); };

            AddLabel("速度", 20, 350, 50);
            numFreq = new NumericUpDown();
            numFreq.Location = new Point(75, 347);
            numFreq.Size = new Size(70, 25);
            numFreq.Minimum = 1;
            numFreq.Maximum = 100;
            numFreq.Value = 20;
            numFreq.ValueChanged += delegate { OnFreqChanged(); };
            host.Controls.Add(numFreq);
            lblFreq = AddLabel("20 次/秒", 155, 350, 165);

            AddLabel("选中键的触发键", 20, 396, 95);
            txtTrigger = AddTextBox(120, 392, 80);
            txtTrigger.ReadOnly = true;
            txtTrigger.Text = "";
            btnPickTrigger = AddButton("设置触发键", 205, 392, 155, 28);
            btnPickTrigger.Click += delegate
            {
                if (listKeys.SelectedIndex < 0)
                {
                    MessageBox.Show("先在上面的列表里选中一个按键，再设置它的触发键。");
                    return;
                }
                StartCapture(2);
            };

            chkHold = new CheckBox();
            chkHold.Text = "长按连点（选中键：勾选=按住才点，不勾=按一下开/再按停）";
            chkHold.Location = new Point(20, 436);
            chkHold.AutoSize = true;
            chkHold.Checked = true;
            chkHold.Enabled = false;
            chkHold.CheckedChanged += delegate
            {
                int i = listKeys.SelectedIndex;
                if (i >= 0 && i < binds.Count)
                {
                    binds[i].hold = chkHold.Checked;
                    SaveIfNotLoading();
                }
            };
            host.Controls.Add(chkHold);

            lblStatus = AddLabel("", 20, 468, 480);
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;

            BuildMacroTab();
        }

        void BuildMacroTab()
        {
            host = tabMacro;

            AddLabel("录制/回放键盘鼠标操作（热键：F8开始/停止，F9回放，F10清除）", 20, 8, 480);

            btnRec = AddButton("开始/停止录制 (F8)", 20, 36, 150, 30);
            btnRec.Click += delegate { ToggleRecording(); };

            btnPlay = AddButton("回放 (F9)", 180, 36, 80, 30);
            btnPlay.Click += delegate { StartPlayback(); };

            btnClearMacro = AddButton("清除 (F10)", 270, 36, 80, 30);
            btnClearMacro.Click += delegate { ClearMacro(); };

            chkFilterProc = new CheckBox();
            chkFilterProc.Text = "仅录制/回放到指定进程（勾选后其他软件不执行）";
            chkFilterProc.Location = new Point(20, 72);
            chkFilterProc.AutoSize = true;
            chkFilterProc.CheckedChanged += delegate { filterEnabled = chkFilterProc.Checked; };
            host.Controls.Add(chkFilterProc);

            AddLabel("目标进程", 20, 104, 60);
            cmbProcess = new ComboBox();
            cmbProcess.Location = new Point(85, 100);
            cmbProcess.Size = new Size(320, 26);
            cmbProcess.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbProcess.SelectedIndexChanged += delegate { OnProcessSelected(); };
            host.Controls.Add(cmbProcess);

            btnRefreshProc = AddButton("刷新", 415, 100, 55, 26);
            btnRefreshProc.Click += delegate { RefreshProcessList(); };

            lblMacroStatus = AddLabel("就绪（F8开始录制）", 20, 130, 480);
            lblMacroStatus.TextAlign = ContentAlignment.MiddleLeft;

            listLog = new ListBox();
            listLog.Location = new Point(20, 156);
            listLog.Size = new Size(480, 210);
            listLog.IntegralHeight = false;
            listLog.HorizontalScrollbar = true;
            host.Controls.Add(listLog);

            AddLabel("宏文件管理（保存后下次直接加载）", 20, 374, 480);

            AddLabel("名字", 20, 402, 40);
            txtMacroName = AddTextBox(60, 402, 140);

            cmbMacroFile = new ComboBox();
            cmbMacroFile.Location = new Point(210, 402);
            cmbMacroFile.Size = new Size(170, 26);
            cmbMacroFile.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbMacroFile.SelectedIndexChanged += delegate
            {
                if (cmbMacroFile.SelectedIndex >= 0 && cmbMacroFile.SelectedItem != null)
                    txtMacroName.Text = cmbMacroFile.SelectedItem.ToString();
            };
            host.Controls.Add(cmbMacroFile);

            btnSaveMacro = AddButton("保存", 390, 402, 65, 28);
            btnSaveMacro.Click += delegate { SaveMacro(); };

            btnLoadMacro = AddButton("加载", 20, 438, 90, 30);
            btnLoadMacro.Click += delegate { LoadMacro(); };

            btnRenameMacro = AddButton("重命名", 120, 438, 90, 30);
            btnRenameMacro.Click += delegate { RenameMacro(); };

            btnDelMacro = AddButton("删除", 220, 438, 90, 30);
            btnDelMacro.Click += delegate { DeleteMacro(); };
        }

        Label AddLabel(string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            l.Size = new Size(w, 25);
            l.AutoSize = false;
            host.Controls.Add(l);
            return l;
        }

        TextBox AddTextBox(int x, int y, int w)
        {
            TextBox t = new TextBox();
            t.Location = new Point(x, y);
            t.Size = new Size(w, 25);
            host.Controls.Add(t);
            return t;
        }

        Button AddButton(string text, int x, int y, int w, int h)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, h);
            host.Controls.Add(b);
            return b;
        }

        // ================= 按键列表管理 =================
        string BindDisplay(Bind b)
        {
            return b.label + " ← " + TriggerName(b.trigger) + "（" + b.freq + "次/秒）";
        }

        void AddBind(int type, int code, int freq, int trigger, bool hold)
        {
            Bind b = new Bind();
            b.type = type;
            b.code = code;
            b.freq = freq;
            b.trigger = trigger;
            b.hold = hold;
            b.label = (type == 1) ? MouseBtnName(code) : KeyToString((Keys)code);

            foreach (Bind x in binds)
            {
                if (x.type == b.type && x.code == b.code) return; // 去重
            }

            binds.Add(b);
            listKeys.Items.Add(BindDisplay(b));
            listKeys.SelectedIndex = listKeys.Items.Count - 1;

            b.thread = new Thread(KeyLoop);
            b.thread.IsBackground = true;
            b.thread.Priority = ThreadPriority.BelowNormal; // 连点线程让出 CPU，系统鼠标输入优先，指针不卡
            b.thread.Start(b);

            SaveIfNotLoading();
        }

        void DeleteSelected()
        {
            int i = listKeys.SelectedIndex;
            if (i < 0) return;
            binds[i].stop = true;
            binds.RemoveAt(i);
            listKeys.Items.RemoveAt(i);

            if (listKeys.Items.Count > 0)
                listKeys.SelectedIndex = Math.Min(i, listKeys.Items.Count - 1);
            else
                OnListSelectedChanged();
            SaveConfig();
        }

        void ClearBinds()
        {
            foreach (Bind b in binds) b.stop = true;
            binds.Clear();
            listKeys.Items.Clear();
            OnListSelectedChanged();
            SaveConfig();
        }

        void OnListSelectedChanged()
        {
            int i = listKeys.SelectedIndex;
            if (i >= 0 && i < binds.Count)
            {
                numFreq.Enabled = true;
                decimal f = binds[i].freq;
                if (f < numFreq.Minimum) f = numFreq.Minimum;
                if (f > numFreq.Maximum) f = numFreq.Maximum;
                numFreq.Value = f;
                lblFreq.Text = binds[i].freq + " 次/秒";
                txtTrigger.Text = TriggerName(binds[i].trigger);
                chkHold.Enabled = true;
                chkHold.Checked = binds[i].hold;
            }
            else
            {
                numFreq.Enabled = false;
                lblFreq.Text = "选中按键调速度";
                txtTrigger.Text = "";
                chkHold.Enabled = false;
            }
        }

        void OnFreqChanged()
        {
            int i = listKeys.SelectedIndex;
            if (i >= 0 && i < binds.Count)
            {
                binds[i].freq = (int)numFreq.Value;
                listKeys.Items[i] = BindDisplay(binds[i]);
                lblFreq.Text = (int)numFreq.Value + " 次/秒";
                // 加载配置时 numFreq.Value 也会触发这里，必须 SaveIfNotLoading，
                // 否则加载中途覆盖 ini，把还没加载完的键（比如切换模式的鼠标左键）丢掉
                SaveIfNotLoading();
            }
        }

        // ================= 捕获输入 =================
        void StartCapture(int mode)
        {
            captureMode = mode;
            btnAddKey.Text = (mode == 1) ? "请按键/点鼠标...(Esc取消)" : "添加按键";
            btnPickTrigger.Text = (mode == 2) ? "请按键/点鼠标...(Esc取消)" : "设置";
            SyncMouseHook();
        }

        void StopCapture()
        {
            captureMode = 0;
            btnAddKey.Text = "添加按键";
            btnPickTrigger.Text = "设置";
            SyncMouseHook();
        }

        // ================= 鼠标钩子动态挂载 =================
        // 低级鼠标钩子会拦截每一个鼠标移动事件，常驻会让指针发钝。
        // 所以只在录制/回放/捕获鼠标键时才挂，平时卸载，让鼠标移动 100% 原生丝滑。
        void EnsureMouseHook()
        {
            if (mouseHook == IntPtr.Zero)
            {
                mouseProc = MouseHookProc;
                mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, IntPtr.Zero, 0);
            }
        }

        void ReleaseMouseHook()
        {
            if (mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(mouseHook);
                mouseHook = IntPtr.Zero;
            }
        }

        void SyncMouseHook()
        {
            bool need = recording || playing || captureMode == 1 || captureMode == 2;
            if (need) EnsureMouseHook();
            else ReleaseMouseHook();
        }

        IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return CallNextHookEx(mouseHook, nCode, wParam, lParam);
            int msg = wParam.ToInt32();
            if (!recording && !playing && captureMode == 0)
                return CallNextHookEx(mouseHook, nCode, wParam, lParam);

            MSLLHOOKSTRUCT info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));

            // 回放中：任何人工鼠标操作（含移动1像素）立即中断回放
            if (playing && (info.flags & LLMHF_INJECTED) == 0)
            {
                manualInterrupt = true;
                StopPlayback();
                return CallNextHookEx(mouseHook, nCode, wParam, lParam);
            }

            // 录制中记录鼠标（跳过程序窗口内的操作，避免点按钮被录进去）
            if (recording && (info.flags & LLMHF_INJECTED) == 0 && !IsInFormRect(info.pt_x, info.pt_y))
            {
                RecordMouse(msg, info);
            }

            if (captureMode == 1 || captureMode == 2)
            {
                int btn = -1;
                if (msg == WM_LBUTTONDOWN) btn = 0;
                else if (msg == WM_RBUTTONDOWN) btn = 1;
                else if (msg == WM_MBUTTONDOWN) btn = 2;

                if (btn >= 0 && (info.flags & LLMHF_INJECTED) == 0)
                {
                    if (captureMode == 1)
                        AddBind(1, btn, 20, (int)Keys.F6, true);
                    else
                        SetSelectedTrigger(MouseToVk(btn));
                    StopCapture();
                    return (IntPtr)1; // 吞掉这次点击
                }
            }
            return CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN || msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    KBDLLHOOKSTRUCT info = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    if ((info.flags & LLKHF_INJECTED) == 0) // 忽略连点器/回放自己注入的按键
                    {
                        if (sendingInput) return CallNextHookEx(keyboardHook, nCode, wParam, lParam);
                        int vk = (int)info.vkCode;
                        bool down = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);

                        // 回放中：任何人工按键立即中断回放（键照常传递）
                        if (playing && down)
                        {
                            manualInterrupt = true;
                            StopPlayback();
                            return CallNextHookEx(keyboardHook, nCode, wParam, lParam);
                        }

                        // 宏录制控制热键（按下时触发）
                        if (down && HandleMacroHotkey(vk)) return (IntPtr)1;

                        // 录制中记录键盘
                        if (recording) RecordKey(vk, down);

                        // 连点器捕获（仅按下时处理）
                        if (down && (captureMode == 1 || captureMode == 2))
                        {
                            if (vk == (int)Keys.Escape)
                            {
                                StopCapture();
                                return (IntPtr)1;
                            }
                            // Win 键忽略（会弹开始菜单）
                            if (vk == (int)Keys.LWin || vk == (int)Keys.RWin)
                                return CallNextHookEx(keyboardHook, nCode, wParam, lParam);

                            if (captureMode == 1)
                                AddBind(0, vk, 20, (int)Keys.F6, true);
                            else
                                SetSelectedTrigger(vk);
                            StopCapture();
                            return (IntPtr)1;
                        }

                    }
                }
            }
            return CallNextHookEx(keyboardHook, nCode, wParam, lParam);
        }

        int MouseToVk(int btn)
        {
            if (btn == 0) return VK_LBUTTON;
            if (btn == 1) return VK_RBUTTON;
            return VK_MBUTTON;
        }

        void SetSelectedTrigger(int vk)
        {
            int i = listKeys.SelectedIndex;
            if (i >= 0 && i < binds.Count)
            {
                binds[i].trigger = vk;
                listKeys.Items[i] = BindDisplay(binds[i]);
                txtTrigger.Text = TriggerName(vk);
                SaveConfig();
            }
        }

        // ================= 生命周期 =================
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            keyboardProc = KeyboardHookProc;
            keyboardHook = SetWindowsHookExKB(WH_KEYBOARD_LL, keyboardProc, IntPtr.Zero, 0);

            stopWatcher = false;

            statusTimer = new System.Windows.Forms.Timer();
            statusTimer.Interval = 100;
            statusTimer.Tick += delegate { UpdateStatus(); };
            statusTimer.Start();

            RefreshProcessList();
            RefreshMacroFileList();
            InitTray();
        }

        // ================= 系统托盘 =================
        void InitTray()
        {
            try
            {
                trayIcon = new NotifyIcon();
                trayIcon.Icon = this.Icon; // 复用 exe 嵌入的图标
                trayIcon.Text = "n1按键宏 " + APP_VERSION;

                trayMenu = new ContextMenuStrip();
                ToolStripMenuItem miShow = new ToolStripMenuItem("打开主界面");
                miShow.Click += delegate { ShowMain(); };
                ToolStripMenuItem miExit = new ToolStripMenuItem("关闭");
                miExit.Click += delegate { reallyExit = true; this.Close(); };
                trayMenu.Items.Add(miShow);
                trayMenu.Items.Add(new ToolStripSeparator());
                trayMenu.Items.Add(miExit);

                trayIcon.ContextMenuStrip = trayMenu;
                trayIcon.DoubleClick += delegate { ShowMain(); };
                trayIcon.Visible = true;
            }
            catch { }
        }

        void ShowMain()
        {
            this.Show();
            if (this.WindowState == FormWindowState.Minimized)
                this.WindowState = FormWindowState.Normal;
            this.Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 点 X 不退出，藏到托盘；只有托盘菜单"关闭"才真正退出
            if (!reallyExit)
            {
                e.Cancel = true;
                this.Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SaveConfig();
            recording = false;
            playing = false;
            stopWatcher = true;
            foreach (Bind b in binds) b.stop = true;
            if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
            if (statusTimer != null) statusTimer.Stop();
            if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); }
            if (trayMenu != null) trayMenu.Dispose();
            base.OnFormClosed(e);
        }

        // ================= 窗口拖拽缩放（字体和控件跟着变大） =================
        void CaptureInitLayout()
        {
            initClient = this.ClientSize;
            initFontSz = this.Font.Size;
            initBounds.Clear();
            CollectAll(tabs, initBounds);
            layoutCaptured = true;
        }

        void CollectAll(Control parent, Dictionary<Control, Rectangle> dict)
        {
            foreach (Control c in parent.Controls)
            {
                dict[c] = c.Bounds;
                CollectAll(c, dict);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!layoutCaptured) return;
            if (initClient.Width <= 0 || initClient.Height <= 0) return;
            int w = this.ClientSize.Width, h = this.ClientSize.Height;
            if (w <= 0 || h <= 0) return; // 最小化时宽高为 0，跳过

            float sx = (float)w / initClient.Width;
            float sy = (float)h / initClient.Height;
            float fs = Math.Min(sx, sy);
            if (fs < 0.35f) fs = 0.35f;
            if (fs > 3f) fs = 3f;

            float newSz = initFontSz * fs;
            if (scaledFont == null || Math.Abs(scaledFont.Size - newSz) > 0.3f)
            {
                Font old = scaledFont;
                scaledFont = new Font(this.Font.FontFamily, newSz);
                this.Font = scaledFont;
                if (old != null) old.Dispose();
            }

            foreach (KeyValuePair<Control, Rectangle> kv in initBounds)
            {
                Control c = kv.Key;
                if (c == null || c.IsDisposed) continue;
                if (c is TabControl || c is TabPage) continue; // 容器交给布局，跳过
                Rectangle r = kv.Value;
                c.Bounds = new Rectangle(
                    (int)Math.Round(r.X * sx),
                    (int)Math.Round(r.Y * sy),
                    (int)Math.Round(r.Width * sx),
                    (int)Math.Round(r.Height * sy));
            }
        }

        // ================= 独立连点逻辑（每个键用自己的触发键） =================
        void KeyLoop(object state)
        {
            Bind b = (Bind)state;
            bool prevHeld = false;
            System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
            while (!b.stop && !stopWatcher)
            {
                // 宏录制/回放期间冻结连点器，避免 DD/SendInput 事件互相污染
                if (recording || playing)
                {
                    b.clicking = false;
                    prevHeld = IsTriggerHeld(b.trigger);
                    Thread.Sleep(10);
                    continue;
                }
                bool held = IsTriggerHeld(b.trigger);
                bool on;
                if (b.hold)
                {
                    on = held;
                }
                else
                {
                    if (held && !prevHeld) b.toggle = !b.toggle;
                    on = b.toggle;
                }
                prevHeld = held;
                b.clicking = on;

                if (on)
                {
                    sw.Restart();
                    ClickOne(b);
                    int jitter = b.rnd.Next(-2, 3); // ±2ms 随机抖动，连点节奏更像真人
                    PreciseWait(sw, Math.Max(1, IntervalOf(b) + jitter));
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
        }

        // 高精度等待：纯 Sleep 节拍，绝不忙等自旋。
        // 自旋（SpinWait）会把 CPU 单核间歇打满，几个键一起连点时全局鼠标卡顿、指针发钝。
        // timeBeginPeriod(1) 已把系统定时器提到 1ms，Sleep 精度对连点完全够用。
        void PreciseWait(System.Diagnostics.Stopwatch sw, int totalMs)
        {
            double remain = totalMs - sw.Elapsed.TotalMilliseconds;
            if (remain <= 0) return;
            Thread.Sleep(Math.Max(1, (int)Math.Round(remain)));
        }

        int PressMs(Bind b)
        {
            return PRESS_MS;
        }

        void ClickOne(Bind b)
        {
            int press = PressMs(b);
            if (b.type == 0)
            {
                sendingInput = true;
                try
                {
                    SendKeyDown((Keys)b.code);
                    Thread.Sleep(press);
                    SendKeyUp((Keys)b.code);
                    Thread.Sleep(2); // 等 DD 键事件到达钩子再解除
                }
                finally { sendingInput = false; }
            }
            else
            {
                // 瞬点：down/up 间隔 2ms，干净利落。游戏内光标不再一直显示按下状态，鼠标移动不卡不降 DPI。
                SendMouseEvent(b.code, false);
                Thread.Sleep(MOUSE_CLICK_GAP);
                SendMouseEvent(b.code, true);
            }
        }

        bool IsTriggerHeld(int vk)
        {
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        // ================= 宏录制/回放 =================
        bool HandleMacroHotkey(int vk)
        {
            if (vk == (int)Keys.F8) { ToggleRecording(); return true; }
            if (vk == (int)Keys.F9) { StartPlayback(); return true; }
            if (vk == (int)Keys.F10) { ClearMacro(); return true; }
            return false;
        }

        // ================= 宏日志 / 进程过滤 =================
        void Log(string msg)
        {
            if (listLog == null) return;
            if (this.InvokeRequired)
            {
                try { BeginInvoke((Action)delegate { Log(msg); }); } catch { }
                return;
            }
            listLog.Items.Add(msg);
            if (listLog.Items.Count > 500) listLog.Items.RemoveAt(0);
            listLog.TopIndex = listLog.Items.Count - 1;
        }

        bool IsTargetForeground()
        {
            if (!filterEnabled) return true; // 开关关=不过滤
            if (selectedProcId <= 0) return true;
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            return (int)pid == selectedProcId;
        }

        void RefreshProcessList()
        {
            int prev = selectedProcId;
            cmbProcess.Items.Clear();
            cmbProcess.Items.Add("所有进程（不限制）");
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    if (!string.IsNullOrEmpty(p.MainWindowTitle))
                        cmbProcess.Items.Add(p.ProcessName + " [" + p.Id + "] " + p.MainWindowTitle);
                }
                catch { }
            }
            int sel = 0;
            for (int i = 1; i < cmbProcess.Items.Count; i++)
            {
                string s = cmbProcess.Items[i].ToString();
                int a = s.LastIndexOf('[');
                int b = s.LastIndexOf(']');
                if (a >= 0 && b > a)
                {
                    int pid;
                    if (int.TryParse(s.Substring(a + 1, b - a - 1), out pid) && pid == prev)
                    { sel = i; break; }
                }
            }
            cmbProcess.SelectedIndex = sel;
        }

        void OnProcessSelected()
        {
            selectedProcId = 0;
            if (cmbProcess.SelectedIndex > 0)
            {
                string s = cmbProcess.SelectedItem.ToString();
                int a = s.LastIndexOf('[');
                int b = s.LastIndexOf(']');
                if (a >= 0 && b > a)
                    int.TryParse(s.Substring(a + 1, b - a - 1), out selectedProcId);
            }
        }

        void ToggleRecording()
        {
            if (playing) return;
            if (!recording)
            {
                macroEvents.Clear();
                lastTick = Environment.TickCount;
                recording = true;
                Log("[录制] 开始" + (selectedProcId > 0 ? "（目标 " + cmbProcess.SelectedItem + "）" : "（所有进程）"));
            }
            else
            {
                recording = false;
                Log("[录制] 停止，共 " + macroEvents.Count + " 个事件");
            }
            UpdateMacroStatus();
            SyncMouseHook();
        }

        void ClearMacro()
        {
            if (recording || playing) return;
            macroEvents.Clear();
            Log("[清除] 已清除全部宏");
            UpdateMacroStatus();
        }

        void StartPlayback()
        {
            if (recording || playing) return;
            if (macroEvents.Count == 0) return;
            playing = true;
            Log("[回放] 开始，共 " + macroEvents.Count + " 个事件");
            UpdateMacroStatus();
            SyncMouseHook();
            Thread t = new Thread(PlayLoop);
            t.IsBackground = true;
            t.Start();
        }

        void StopPlayback()
        {
            playing = false;
            if (this.InvokeRequired)
            {
                try { BeginInvoke((Action)delegate { UpdateMacroStatus(); }); } catch { }
            }
            else
            {
                UpdateMacroStatus();
            }
            SyncMouseHook();
        }

        void AddMacroEvent(MacroEvent ev)
        {
            int now = Environment.TickCount;
            ev.delay = (macroEvents.Count == 0) ? 0 : (now - lastTick);
            lastTick = now;
            macroEvents.Add(ev);
        }

        void RecordKey(int vk, bool down)
        {
            if (!IsTargetForeground()) return;
            MacroEvent ev = new MacroEvent();
            ev.type = 0;
            ev.action = down ? 0 : 1;
            ev.code = vk;
            AddMacroEvent(ev);
            Log("[键] " + (down ? "按下 " : "抬起 ") + KeyToString((Keys)vk));
        }

        void RecordMouse(int msg, MSLLHOOKSTRUCT info)
        {
            if (!IsTargetForeground()) return;
            MacroEvent ev = new MacroEvent();
            ev.type = 1;
            ev.x = info.pt_x;
            ev.y = info.pt_y;
            string logText = null;
            if (msg == WM_MOUSEMOVE)
            {
                // 移动采样：10ms 一个点，保留轨迹又避免宏爆炸
                int now = Environment.TickCount;
                if (now - lastMoveTick < 10) return;
                lastMoveTick = now;
                ev.action = 0;
            }
            else if (msg == WM_LBUTTONDOWN) { ev.action = 1; ev.code = 0; logText = "[鼠] 左键按下"; }
            else if (msg == WM_LBUTTONUP) { ev.action = 2; ev.code = 0; logText = "[鼠] 左键抬起"; }
            else if (msg == WM_RBUTTONDOWN) { ev.action = 3; ev.code = 1; logText = "[鼠] 右键按下"; }
            else if (msg == WM_RBUTTONUP) { ev.action = 4; ev.code = 1; logText = "[鼠] 右键抬起"; }
            else if (msg == WM_MBUTTONDOWN) { ev.action = 5; ev.code = 2; logText = "[鼠] 中键按下"; }
            else if (msg == WM_MBUTTONUP) { ev.action = 6; ev.code = 2; logText = "[鼠] 中键抬起"; }
            else if (msg == WM_MOUSEWHEEL) { ev.action = 7; ev.wheel = (short)(info.mouseData >> 16); logText = "[鼠] 滚轮 " + ev.wheel; }
            else return;
            AddMacroEvent(ev);
            if (logText != null) Log(logText);
        }

        bool IsInFormRect(int x, int y)
        {
            RECT r;
            if (!GetWindowRect(this.Handle, out r)) return false;
            return (x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom);
        }

        void PlayLoop()
        {
            foreach (MacroEvent ev in macroEvents)
            {
                if (!playing) break;
                if (ev.delay > 0) Thread.Sleep(ev.delay);
                if (!playing) break;
                if (!IsTargetForeground()) { manualInterrupt = true; break; }
                if (ev.type == 0) PlayKey(ev);
                else PlayMouse(ev);
            }
            playing = false;
            bool wasInterrupt = manualInterrupt;
            manualInterrupt = false;
            try { BeginInvoke((Action)delegate { Log(wasInterrupt ? "[回放] 人工中断，已复位" : "[回放] 结束"); UpdateMacroStatus(); }); } catch { }
        }

        void PlayKey(MacroEvent ev)
        {
            sendingInput = true;
            try
            {
                if (ev.action == 0) SendKeyDown((Keys)ev.code);
                else SendKeyUp((Keys)ev.code);
                Thread.Sleep(2); // 等 DD 键事件到达钩子再解除
            }
            finally { sendingInput = false; }
        }

        void PlayMouse(MacroEvent ev)
        {
            INPUT input = new INPUT();
            input.type = INPUT_MOUSE;
            input.U.mi.dx = ev.x;
            input.U.mi.dy = ev.y;
            input.U.mi.mouseData = 0;
            input.U.mi.dwFlags = 0;
            input.U.mi.time = 0;
            input.U.mi.dwExtraInfo = IntPtr.Zero;

            switch (ev.action)
            {
                case 0: // 移动（绝对坐标 → 映射整个虚拟桌面，修正偏移）
                    {
                        Rectangle vs = SystemInformation.VirtualScreen;
                        input.U.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
                        // 归一化范围 0~65535，分母用 Width 不是 Width-1，除数用 65536，否则每帧 ±1px 累积成偏移
                        input.U.mi.dx = (int)Math.Round((ev.x - vs.X) * 65536.0 / vs.Width);
                        input.U.mi.dy = (int)Math.Round((ev.y - vs.Y) * 65536.0 / vs.Height);
                        if (input.U.mi.dx < 0) input.U.mi.dx = 0;
                        if (input.U.mi.dy < 0) input.U.mi.dy = 0;
                        if (input.U.mi.dx > 65535) input.U.mi.dx = 65535;
                        if (input.U.mi.dy > 65535) input.U.mi.dy = 65535;
                    }
                    break;
                case 1: input.U.mi.dwFlags = MOUSEEVENTF_LEFTDOWN; break;
                case 2: input.U.mi.dwFlags = MOUSEEVENTF_LEFTUP; break;
                case 3: input.U.mi.dwFlags = MOUSEEVENTF_RIGHTDOWN; break;
                case 4: input.U.mi.dwFlags = MOUSEEVENTF_RIGHTUP; break;
                case 5: input.U.mi.dwFlags = MOUSEEVENTF_MIDDLEDOWN; break;
                case 6: input.U.mi.dwFlags = MOUSEEVENTF_MIDDLEUP; break;
                case 7: // 滚轮
                    input.U.mi.dwFlags = MOUSEEVENTF_WHEEL;
                    input.U.mi.mouseData = (uint)ev.wheel;
                    break;
            }
            lock (sendLock) { SendInput(1, new INPUT[] { input }, Marshal.SizeOf(typeof(INPUT))); }
        }

        void UpdateMacroStatus()
        {
            if (recording)
                lblMacroStatus.Text = "● 录制中… 已录 " + macroEvents.Count + " 事件（F8停止）";
            else if (playing)
                lblMacroStatus.Text = "▶ 回放中…";
            else if (macroEvents.Count > 0)
                lblMacroStatus.Text = "已录 " + macroEvents.Count + " 事件（F9回放 / F10清除）";
            else
                lblMacroStatus.Text = "就绪（F8开始录制）";
        }

        // ================= 宏文件保存/加载/重命名/删除 =================
        string MacroDir()
        {
            string dir = Path.Combine(Application.StartupPath, "macros");
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        string SanitizeFileName(string name)
        {
            char[] bad = new char[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
            foreach (char c in bad) name = name.Replace(c, '_');
            return name.Trim();
        }

        void RefreshMacroFileList()
        {
            string prev = (cmbMacroFile.SelectedIndex >= 0 && cmbMacroFile.SelectedItem != null) ? cmbMacroFile.SelectedItem.ToString() : "";
            cmbMacroFile.Items.Clear();
            try
            {
                foreach (string f in Directory.GetFiles(MacroDir(), "*.n1m"))
                    cmbMacroFile.Items.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch { }
            if (cmbMacroFile.Items.Count > 0)
            {
                int idx = 0;
                for (int i = 0; i < cmbMacroFile.Items.Count; i++)
                    if (cmbMacroFile.Items[i].ToString() == prev) { idx = i; break; }
                cmbMacroFile.SelectedIndex = idx;
                if (txtMacroName.Text == "") txtMacroName.Text = cmbMacroFile.SelectedItem.ToString();
            }
        }

        void SaveMacro()
        {
            if (macroEvents.Count == 0) { MessageBox.Show("还没有录制的宏，先录制再保存。"); return; }
            string name = SanitizeFileName(txtMacroName.Text);
            if (name == "") { MessageBox.Show("请输入宏名字。"); return; }
            try
            {
                string path = Path.Combine(MacroDir(), name + ".n1m");
                using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                using (BinaryWriter w = new BinaryWriter(fs))
                {
                    w.Write(new byte[] { (byte)'N', (byte)'1', (byte)'M' });
                    w.Write(1); // 格式版本
                    w.Write(macroEvents.Count);
                    foreach (MacroEvent ev in macroEvents)
                    {
                        w.Write(ev.delay);
                        w.Write(ev.type);
                        w.Write(ev.action);
                        w.Write(ev.code);
                        w.Write(ev.x);
                        w.Write(ev.y);
                        w.Write(ev.wheel);
                    }
                }
                Log("[保存] 宏已保存为 " + name + "（" + macroEvents.Count + " 事件）");
                RefreshMacroFileList();
            }
            catch (Exception ex) { MessageBox.Show("保存失败：" + ex.Message); }
        }

        void LoadMacro()
        {
            if (recording || playing) return;
            if (cmbMacroFile.SelectedIndex < 0) { MessageBox.Show("先选择要加载的宏。"); return; }
            string name = cmbMacroFile.SelectedItem.ToString();
            try
            {
                string path = Path.Combine(MacroDir(), name + ".n1m");
                List<MacroEvent> list = new List<MacroEvent>();
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (BinaryReader r = new BinaryReader(fs))
                {
                    byte[] magic = r.ReadBytes(3);
                    if (magic.Length < 3 || magic[0] != 'N' || magic[1] != '1' || magic[2] != 'M')
                        throw new Exception("不是有效的宏文件");
                    r.ReadInt32(); // 版本
                    int n = r.ReadInt32();
                    for (int i = 0; i < n; i++)
                    {
                        MacroEvent ev = new MacroEvent();
                        ev.delay = r.ReadInt32();
                        ev.type = r.ReadInt32();
                        ev.action = r.ReadInt32();
                        ev.code = r.ReadInt32();
                        ev.x = r.ReadInt32();
                        ev.y = r.ReadInt32();
                        ev.wheel = r.ReadInt32();
                        list.Add(ev);
                    }
                }
                macroEvents = list;
                txtMacroName.Text = name;
                Log("[加载] 已加载宏 " + name + "（" + macroEvents.Count + " 事件）");
                UpdateMacroStatus();
            }
            catch (Exception ex) { MessageBox.Show("加载失败：" + ex.Message); }
        }

        void RenameMacro()
        {
            if (cmbMacroFile.SelectedIndex < 0) { MessageBox.Show("先选择要重命名的宏。"); return; }
            string oldName = cmbMacroFile.SelectedItem.ToString();
            string newName = SanitizeFileName(txtMacroName.Text);
            if (newName == "" || newName == oldName) { MessageBox.Show("请输入新的名字。"); return; }
            try
            {
                string oldPath = Path.Combine(MacroDir(), oldName + ".n1m");
                string newPath = Path.Combine(MacroDir(), newName + ".n1m");
                if (File.Exists(newPath)) { MessageBox.Show("这个名字已存在。"); return; }
                File.Move(oldPath, newPath);
                Log("[重命名] " + oldName + " → " + newName);
                RefreshMacroFileList();
            }
            catch (Exception ex) { MessageBox.Show("重命名失败：" + ex.Message); }
        }

        void DeleteMacro()
        {
            if (cmbMacroFile.SelectedIndex < 0) { MessageBox.Show("先选择要删除的宏。"); return; }
            string name = cmbMacroFile.SelectedItem.ToString();
            try
            {
                File.Delete(Path.Combine(MacroDir(), name + ".n1m"));
                Log("[删除] 已删除宏 " + name);
                RefreshMacroFileList();
            }
            catch (Exception ex) { MessageBox.Show("删除失败：" + ex.Message); }
        }

        int IntervalOf(Bind b)
        {
            int f = b.freq;
            if (f < 1) f = 1;
            return Math.Max(1, 1000 / f);
        }

        // ================= 发送按键（SendInput） =================
        void SendKeyDown(Keys k)
        {
            uint scan = MapVirtualKey((uint)k, 0);
            INPUT input = new INPUT();
            input.type = INPUT_KEYBOARD;
            input.U.ki.wVk = 0;
            input.U.ki.wScan = (ushort)scan;
            input.U.ki.dwFlags = KEYEVENTF_SCANCODE;
            if (IsExtendedKey(k)) input.U.ki.dwFlags |= KEYEVENTF_EXTENDEDKEY;
            input.U.ki.time = 0;
            input.U.ki.dwExtraInfo = IntPtr.Zero;
            lock (sendLock) { SendInput(1, new INPUT[] { input }, Marshal.SizeOf(typeof(INPUT))); }
        }

        void SendKeyUp(Keys k)
        {
            uint scan = MapVirtualKey((uint)k, 0);
            INPUT input = new INPUT();
            input.type = INPUT_KEYBOARD;
            input.U.ki.wVk = 0;
            input.U.ki.wScan = (ushort)scan;
            input.U.ki.dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP;
            if (IsExtendedKey(k)) input.U.ki.dwFlags |= KEYEVENTF_EXTENDEDKEY;
            input.U.ki.time = 0;
            input.U.ki.dwExtraInfo = IntPtr.Zero;
            lock (sendLock) { SendInput(1, new INPUT[] { input }, Marshal.SizeOf(typeof(INPUT))); }
        }

        void SendMouseEvent(int btn, bool up)
        {
            // 鼠标点击走 SendInput：DD 虚拟鼠标走 Raw Input 的游戏读不到，SendInput 前台稳定可靠
            INPUT input = new INPUT();
            input.type = INPUT_MOUSE;
            input.U.mi.dx = 0;
            input.U.mi.dy = 0;
            input.U.mi.mouseData = 0;
            input.U.mi.dwFlags = MouseFlag(btn, up);
            input.U.mi.time = 0;
            input.U.mi.dwExtraInfo = IntPtr.Zero;
            lock (sendLock) { SendInput(1, new INPUT[] { input }, Marshal.SizeOf(typeof(INPUT))); }
        }

        uint MouseFlag(int btn, bool up)
        {
            switch (btn)
            {
                case 0: return up ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_LEFTDOWN;
                case 1: return up ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_RIGHTDOWN;
                default: return up ? MOUSEEVENTF_MIDDLEUP : MOUSEEVENTF_MIDDLEDOWN;
            }
        }

        bool IsExtendedKey(Keys k)
        {
            switch (k)
            {
                case Keys.Insert:
                case Keys.Delete:
                case Keys.Home:
                case Keys.End:
                case Keys.PageUp:
                case Keys.PageDown:
                case Keys.Left:
                case Keys.Right:
                case Keys.Up:
                case Keys.Down:
                case Keys.NumLock:
                case Keys.RControlKey:
                case Keys.RMenu:
                case Keys.Divide:
                    return true;
                default:
                    return false;
            }
        }

        // ================= 配置保存/读取 =================
        string ConfigPath()
        {
            return System.IO.Path.Combine(Application.StartupPath, "n1按键宏.ini");
        }

        void SaveConfig()
        {
            try
            {
                List<string> lines = new List<string>();
                foreach (Bind b in binds)
                {
                    lines.Add("key=" + b.type + "," + b.code + "," + b.freq + "," + b.trigger + "," + (b.hold ? "1" : "0"));
                }
                System.IO.File.WriteAllLines(ConfigPath(), lines.ToArray(), System.Text.Encoding.UTF8);
            }
            catch { }
        }

        void SaveIfNotLoading()
        {
            if (!loading) SaveConfig();
        }

        bool LoadConfig()
        {
            loading = true;
            try
            {
                string path = ConfigPath();
                if (!System.IO.File.Exists(path)) return false;

                string[] lines = System.IO.File.ReadAllLines(path);
                bool loaded = false;
                foreach (string line in lines)
                {
                    string s = line.Trim();
                    if (s.Length == 0 || s[0] == '#') continue;
                    int eq = s.IndexOf('=');
                    if (eq < 0) continue;
                    string key = s.Substring(0, eq).Trim();
                    string val = s.Substring(eq + 1).Trim();

                    if (key == "key")
                    {
                        string[] parts = val.Split(',');
                        if (parts.Length >= 3)
                        {
                            int type, code, freq, trig = (int)Keys.F6;
                            bool hold = true;
                            if (int.TryParse(parts[0], out type) &&
                                int.TryParse(parts[1], out code) &&
                                int.TryParse(parts[2], out freq))
                            {
                                if (parts.Length >= 4) int.TryParse(parts[3], out trig);
                                if (parts.Length >= 5) hold = (parts[4] == "1");
                                if (freq < 1) freq = 1;
                                if (freq > 100) freq = 100;
                                AddBind(type, code, freq, trig, hold);
                                loaded = true;
                            }
                        }
                    }
                }
                return loaded;
            }
            catch { return false; }
            finally
            {
                loading = false;
            }
        }

        // ================= 显示辅助 =================
        void UpdateStatus()
        {
            int n = 0;
            foreach (Bind b in binds) if (b.clicking) n++;
            if (n > 0)
            {
                lblStatus.Text = "● " + n + " 个键连点中…";
                lblStatus.ForeColor = Color.ForestGreen;
            }
            else
            {
                lblStatus.Text = "○ 已就绪 — 选中按键设置触发键/速度/连点模式";
                lblStatus.ForeColor = Color.Gray;
            }
        }

        string TriggerName(int vk)
        {
            if (vk == VK_LBUTTON) return "鼠标左键";
            if (vk == VK_RBUTTON) return "鼠标右键";
            if (vk == VK_MBUTTON) return "鼠标中键";
            return KeyToString((Keys)vk);
        }

        string KeyToString(Keys k)
        {
            if (k >= Keys.A && k <= Keys.Z) return ((char)('A' + (k - Keys.A))).ToString();
            if (k >= Keys.D0 && k <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return "小键盘" + (char)('0' + (k - Keys.NumPad0));
            if (k >= Keys.F1 && k <= Keys.F24) return "F" + (k - Keys.F1 + 1);
            switch (k)
            {
                case Keys.Space: return "空格";
                case Keys.Enter: return "回车";
                case Keys.Escape: return "Esc";
                case Keys.Tab: return "Tab";
                case Keys.Back: return "退格";
                case Keys.Left: return "←方向键";
                case Keys.Right: return "→方向键";
                case Keys.Up: return "↑方向键";
                case Keys.Down: return "↓方向键";
                case Keys.ControlKey:
                case Keys.LControlKey:
                case Keys.RControlKey: return "Ctrl";
                case Keys.Menu:
                case Keys.LMenu:
                case Keys.RMenu: return "Alt";
                case Keys.ShiftKey:
                case Keys.LShiftKey:
                case Keys.RShiftKey: return "Shift";
                case Keys.Delete: return "Delete";
                case Keys.Insert: return "Insert";
                case Keys.Home: return "Home";
                case Keys.End: return "End";
                case Keys.PageUp: return "PgUp";
                case Keys.PageDown: return "PgDn";
                default: return k.ToString();
            }
        }

        string MouseBtnName(int btn)
        {
            if (btn == 0) return "鼠标左键";
            if (btn == 1) return "鼠标右键";
            return "鼠标中键";
        }
    }
}
