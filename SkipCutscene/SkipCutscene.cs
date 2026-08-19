using System;
using System.Linq;
using Dalamud;
using Dalamud.Game;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace SkipCutscene;

public class SkipCutscene : IDalamudPlugin
{
    // ── 為什麼是「三個函式 × 每函式多道閘門」 ──────────────────────────────
    // 台服 7.20 有三個內容完全一樣、名字都叫 "IsPlayCutscene" 的取值器,分屬
    // cutscene/event director 子系統的三個 script 類別(離線在執行檔裡逐一以
    // 註冊字串證實:0x140B5D450 / 0x141972160 / 0x140CC90E0,三者都被同一個
    // master initializer 註冊,配對字串逐字都是 "IsPlayCutscene")。不同導演
    // 型別的過場分派到不同實作 —— 只 patch 其中一個會漏掉走另外兩個的過場,
    // 這正是「有些過場略不掉」的成因。
    //
    // 每個 IsPlayCutscene 的結構相同:回傳的 bl 預設 = 1(「播放此過場」),
    // 函式體裡有若干道條件跳躍,一旦條件成立就短路跳到 epilogue、讓 bl 維持
    // 1(強制播放),繞過後面「這個過場該不該略過」的逐過場判定呼叫。本外掛
    // 把這些「維持 bl=1」的閘門 NOP 掉,讓逐過場判定得以生效 —— 這就是原本
    // 已在函式 A 上驗證有效的做法,現在對稱地補齊 A 的第三道閘門、並延伸到 B/C。
    //
    // 🔴 為什麼用「函式錨 + 固定偏移」而不是「每道閘門一條特徵碼」:
    //   三個函式的閘門群位元組完全相同(cmp dword [rax+0x18],0 / je / cmp
    //   dword [rax+0x20],0 / je),單看閘門的位元組無法區分是哪個函式;唯一
    //   能區分的是各函式**專屬的前導碼**。所以每個函式用一條落在專屬前導碼
    //   的錨特徵碼(離線驗證過在全映像唯一命中、且不含任何 rip 相對位移位元組
    //   ⇒ 改版不會因為位址挪動而把錨指到別處),閘門位址 = 錨 + 固定偏移,
    //   每道閘門在寫入前仍逐字讀回比對(見 TryReadSite)。
    //
    // 🔴 錨都落在「不會被本外掛改寫」的位元組上(我們只改 je/jne 那兩個 byte),
    //   所以即使上一次載入沒還原乾淨(閘門停在 90 90),重載時錨照樣命中,
    //   讀回比對再把 90 90 認成「已 patch」而不誤報。

    // 一道閘門 = 一個 2-byte 條件跳躍。NOP 後等長(90 90),不引入任何新的
    // 記憶體解參考,語意上只是讓後面的逐過場判定得以執行。
    private sealed class Gate(string label, int offset, byte[] original)
    {
        public string Label { get; } = label;

        /// <summary>相對於函式錨的偏移(離線驗證)。</summary>
        public int Offset { get; } = offset;

        /// <summary>這個位置的原始跳躍位元組(逐字)。</summary>
        public byte[] Original { get; } = original;

        /// <summary>解析後的絕對位址 = 錨 + Offset。</summary>
        public nint Address { get; set; }
    }

    // 一個函式 = 一個 IsPlayCutscene 取值器;錨特徵碼 + 它底下的所有閘門。
    private sealed class FunctionPatch(string name, string anchorSig, Gate[] gates)
    {
        public string Name { get; } = name;

        public string AnchorSig { get; } = anchorSig;

        public Gate[] Gates { get; } = gates;

        /// <summary>錨找到 ＋ 所有閘門都通過位元組驗證。false ＝ 這個函式一個 byte 都不會寫。</summary>
        public bool Armed { get; set; }

        /// <summary>我們相信這個函式的 patch 目前套用中(只用來誠實回報殘留狀態)。</summary>
        public bool Applied { get; set; }
    }

    // patch 內容:把 2-byte 條件跳躍換成兩個 nop。所有閘門共用。
    private static readonly byte[] Nop2 = [0x90, 0x90];

    // ── 三個 IsPlayCutscene 的錨與閘門(全部以台服 7.20 執行檔離線驗證) ──────
    //   D:/FINAL FANTASY XIV TC/game/ffxiv_dx11.exe,image base 0x140000000。
    //   錨特徵碼在 .text 唯一命中;各閘門在錨+偏移處的原始位元組如下。
    //
    //   A  取值器 0x140B5D450   錨 0x140B5D49C  = mov rcx,[rax]; test [rcx+0x344],bl
    //        A-G1 +0x1C 0x140B5D4B8  75 39  jne  (虛擬呼叫 [rax+0xa48] 結果閘門)
    //        A-G3 +0x37 0x140B5D4D3  74 1E  je   (cmp [rax+0x18],0 —— 新增)
    //        A-G2 +0x3D 0x140B5D4D9  74 18  je   (cmp [rax+0x20],0)
    //   B  取值器 0x141972160   錨 0x1419721AF  = mov rdx,[rcx]; mov r8,[rdx+0xad8]
    //        B-G1 +0x11 0x1419721C0  75 39  jne
    //        B-G3 +0x2C 0x1419721DB  74 1E  je
    //        B-G2 +0x32 0x1419721E1  74 18  je
    //   C  取值器 0x140CC90E0   錨 0x140CC910F  = mov edx,0xde; add rcx,0x10; mov rdi,rax
    //        C-G3 +0x15 0x140CC9124  74 1E  je   (C 沒有虛擬呼叫閘門)
    //        C-G2 +0x1B 0x140CC912A  74 18  je
    private readonly FunctionPatch[] _functions =
    [
        new FunctionPatch("IsPlayCutscene(A)", "48 8B 08 84 99 44 03 00 00",
        [
            new Gate("A-G1", 0x1C, [0x75, 0x39]),
            new Gate("A-G3", 0x37, [0x74, 0x1E]),
            new Gate("A-G2", 0x3D, [0x74, 0x18]),
        ]),
        new FunctionPatch("IsPlayCutscene(B)", "48 8B 11 4C 8B 82 D8 0A 00 00",
        [
            new Gate("B-G1", 0x11, [0x75, 0x39]),
            new Gate("B-G3", 0x2C, [0x74, 0x1E]),
            new Gate("B-G2", 0x32, [0x74, 0x18]),
        ]),
        new FunctionPatch("IsPlayCutscene(C)", "BA DE 00 00 00 48 83 C1 10 48 8B F8",
        [
            new Gate("C-G3", 0x15, [0x74, 0x1E]),
            new Gate("C-G2", 0x1B, [0x74, 0x18]),
        ]),
    ];

    private readonly Config _config;
    private readonly IPluginLog _log;

    private bool _commandRegistered;

    public SkipCutscene(IPluginLog pluginLog)
    {
        _log = pluginLog;

        if (Interface.GetPluginConfig() is not Config configuration || configuration.Version == 0)
            configuration = new Config { IsEnabled = true, Version = 1 };

        _config = configuration;

        Localization.Init(Interface.AssemblyLocation.DirectoryName);

        var moduleBase = SigScanner.Module.BaseAddress.ToInt64();
        foreach (var fn in _functions)
            ResolveFunction(fn, moduleBase);

        if (_functions.All(f => !f.Armed))
        {
            _log.Information(
                "[SkipCutscene] 找不到任何 IsPlayCutscene 的特徵碼,已停用以保護遊戲——可能是遊戲改版。");
            RegisterCommand();
            return;
        }

        _log.Information(
            "[SkipCutscene] 就緒:{0}/{1} 個 IsPlayCutscene 取值器通過位元組驗證。",
            _functions.Count(f => f.Armed),
            _functions.Length);

        if (_config.IsEnabled)
            SetEnabled(true);

        RegisterCommand();
    }

    public void Dispose()
    {
        if (_commandRegistered)
        {
            CommandManager.RemoveHandler("/sc");
            _commandRegistered = false;
        }

        // 還原路徑同樣要驗:SetEnabled(false) 內部先確認每道閘門現場是「原始」或
        // 「我們寫進去的 90 90」兩者之一,不符就整個函式放棄 —— 絕不盲寫回去。
        SetEnabled(false);

        if (_functions.Any(f => f.Applied))
        {
            _log.Information(
                "[SkipCutscene] 部分還原被拒絕(現場與預期不符),patch 可能仍留在遊戲記憶體中;" +
                "重新啟動遊戲即可完全復原。");
        }

        GC.SuppressFinalize(this);
    }

    public string Name => "SkipCutscene";

    [PluginService] public IDalamudPluginInterface Interface { get; private set; }

    [PluginService] public ISigScanner SigScanner { get; private set; }

    [PluginService] public ICommandManager CommandManager { get; private set; }

    [PluginService] public IChatGui ChatGui { get; private set; }

    /// <summary>對每個已武裝的函式套用 / 還原 patch。</summary>
    public void SetEnabled(bool isEnable)
    {
        foreach (var fn in _functions)
            SetFunctionEnabled(fn, isEnable);
    }

    private void ResolveFunction(FunctionPatch fn, long moduleBase)
    {
        // ScanText 找不到時擲 KeyNotFoundException,不是回 IntPtr.Zero;用 TryScanText
        // 才能把「特徵碼斷了」變成一行看得到的 Information 而不是建構子直接爆掉。
        if (!SigScanner.TryScanText(fn.AnchorSig, out var anchor))
        {
            _log.Information("[SkipCutscene] {0} 錨特徵碼找不到,略過此函式(可能是遊戲改版)。", fn.Name);
            return;
        }

        _log.Information(
            "[SkipCutscene] {0} 錨 [\"ffxiv_dx11.exe\"+{1}]",
            fn.Name,
            (anchor.ToInt64() - moduleBase).ToString("X"));

        // fail-closed:寫任何一個 byte 之前,先確認這個函式的每一道閘門現場都是
        // 我們認得的東西。任何一道對不上 → 整個函式不武裝(絕不半套 patch)。
        var allAlreadyPatched = true;
        foreach (var gate in fn.Gates)
        {
            gate.Address = anchor + gate.Offset;
            if (!TryReadSite(fn.Name, gate, out var current))
                return;
            if (!BytesEqual(current, Nop2))
                allAlreadyPatched = false;
        }

        fn.Armed = true;

        // 全部已經是 90 90 = 上一次載入沒還原乾淨(或別的外掛也 patch 了)。
        // 不是錯誤,只是「已生效」,不要重複寫也不要誤報。
        fn.Applied = allAlreadyPatched;

        _log.Information("[SkipCutscene] {0} 全部 {1} 道閘門與預期相符,可用。", fn.Name, fn.Gates.Length);
    }

    private void SetFunctionEnabled(FunctionPatch fn, bool isEnable)
    {
        if (!fn.Armed) return;

        // 第一階段:這個函式的每一道閘門全部驗過才動手。半套 patch(一道改了一道
        // 沒改)會留下我們自己也還原不回去的狀態,所以只要有一道對不上,一個 byte
        // 都不寫,並解除這個函式的武裝。
        var currents = new byte[fn.Gates.Length][];
        for (var i = 0; i < fn.Gates.Length; i++)
        {
            if (TryReadSite(fn.Name, fn.Gates[i], out currents[i]))
                continue;

            fn.Armed = false;
            _log.Information(
                "[SkipCutscene] {0} 目標指令與預期不符,已停用此函式以保護遊戲——可能是遊戲改版。",
                fn.Name);
            return;
        }

        // 第二階段:只寫真的需要改的位元組。
        var allWritten = true;
        for (var i = 0; i < fn.Gates.Length; i++)
        {
            var desired = isEnable ? Nop2 : fn.Gates[i].Original;
            if (!WriteSite(fn.Name, fn.Gates[i], currents[i], desired))
                allWritten = false;
        }

        if (allWritten)
            fn.Applied = isEnable;
    }

    private void RegisterCommand()
    {
        _commandRegistered = CommandManager.AddHandler("/sc", new CommandInfo(OnCommand)
        {
            HelpMessage = "/sc: skip cutscene enable/disable.".Loc(),
        });
    }

    /// <summary>
    ///     讀回目標位址現在的位元組,並與「原始」「已 patch」兩組期望值逐字比對。
    ///     兩者皆不符 ＝ 不認得的現場,回 false(呼叫端一律停用該函式的寫入能力)。
    /// </summary>
    private bool TryReadSite(string fnName, Gate gate, out byte[] current)
    {
        current = [];

        if (gate.Address == nint.Zero)
        {
            _log.Information("[SkipCutscene] {0}/{1} 位址是 0,已停用以保護遊戲。", fnName, gate.Label);
            return false;
        }

        // ReadProcessMemory 由核心驗證可讀性,位址壞掉是回 false 而不是 AccessViolation。
        if (!SafeMemory.ReadBytes(gate.Address, gate.Original.Length, out var buffer) ||
            buffer.Length != gate.Original.Length)
        {
            _log.Information(
                "[SkipCutscene] {0}/{1} 讀取失敗(位址 {2}),已停用以保護遊戲。",
                fnName,
                gate.Label,
                gate.Address.ToInt64().ToString("X"));
            return false;
        }

        current = buffer;

        if (BytesEqual(buffer, gate.Original) || BytesEqual(buffer, Nop2))
            return true;

        _log.Information(
            "[SkipCutscene] {0}/{1} 目標指令與預期不符:位址 {2} 現在是 [{3}],預期原始 [{4}] 或已 patch [{5}]。",
            fnName,
            gate.Label,
            gate.Address.ToInt64().ToString("X"),
            Hex(buffer),
            Hex(gate.Original),
            Hex(Nop2));
        return false;
    }

    /// <summary>
    ///     把 <paramref name="desired" /> 寫進去。現場已經是目標值就不重複寫。
    ///     回傳「現場現在確實等於 desired」。
    /// </summary>
    private bool WriteSite(string fnName, Gate gate, byte[] current, byte[] desired)
    {
        if (BytesEqual(current, desired))
            return true;

        if (SafeMemory.WriteBytes(gate.Address, desired))
            return true;

        _log.Information(
            "[SkipCutscene] {0}/{1} 寫入失敗(位址 {2},WriteProcessMemory 回 false)。",
            fnName,
            gate.Label,
            gate.Address.ToInt64().ToString("X"));
        return false;
    }

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }

        return true;
    }

    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace('-', ' ');

    private void OnCommand(string command, string arguments)
    {
        if (!string.Equals(command, "/sc", StringComparison.OrdinalIgnoreCase)) return;

        if (_functions.All(f => !f.Armed))
        {
            ChatGui.PrintError(
                "Skip Cutscene: disabled to protect the game - the target instructions do not match what this plugin expects (game update?)."
                    .Loc());
            return;
        }

        var desired = !_config.IsEnabled;
        SetEnabled(desired);

        // SetEnabled 可能在這一刻才發現現場對不上而解除所有函式的武裝 —— 那就不要謊報成功。
        if (_functions.All(f => !f.Armed))
        {
            ChatGui.PrintError(
                "Skip Cutscene: disabled to protect the game - the target instructions do not match what this plugin expects (game update?)."
                    .Loc());
            return;
        }

        _config.IsEnabled = desired;
        Interface.SavePluginConfig(_config);
        ChatGui.Print(desired ? "Skip Cutscene: Enabled".Loc() : "Skip Cutscene: Disabled".Loc());
    }
}
