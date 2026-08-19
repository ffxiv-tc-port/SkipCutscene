using System;
using Dalamud;
using Dalamud.Game;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace SkipCutscene;

public class SkipCutscene : IDalamudPlugin
{
    // ── 特徵碼 ────────────────────────────────────────────────────────────
    // 兩條都是「多重命中」的特徵碼(Dalamud 的 ScanText 取位址最低的那一個):
    //   Sig1 在台服 7.20 有 2 個命中,Sig2 有 3 個。取第一個之後兩者剛好落在同一個
    //   函式 [0x140B5D450, 0x140B5D51A) 內,這是本外掛依賴的前提。
    //   下面的位元組驗證就是在保護這個前提 —— 命中飄到別的函式時位元組不會剛好對上。
    private const string Sig1 =
        "75 ?? 48 8b 0d ?? ?? ?? ?? ba ?? 00 00 00 48 83 c1 10 e8 ?? ?? ?? ?? 83 78 ?? ?? 74";

    private const string Sig2 = "74 18 8B D7 48 8D 0D";

    // ── 目標指令的期望位元組 ──────────────────────────────────────────────
    // 以台服 7.20 執行檔離線驗證(D:/FINAL FANTASY XIV TC/game/ffxiv_dx11.exe,
    // image base 0x140000000):
    //   Sig1 第一個命中 → ffxiv_dx11.exe+0xB5D4B8   75 39   jne 0x140B5D4F3
    //   Sig2 第一個命中 → ffxiv_dx11.exe+0xB5D4D9   74 18   je  0x140B5D4F3
    // patch 內容是把這兩個條件跳躍換成 90 90(nop nop),讓兩道「這段過場不可略過」
    // 的閘門直接落空。
    //
    // 🔴 這兩個常數存在的理由:**特徵碼命中不代表命中處還是同一條指令。**
    //    Sig1 的第二個 byte 是萬用字元(??),遊戲改版後只要跳躍位移變了,特徵碼照樣
    //    命中,但「還原時寫回 75 39」就會把錯的位移寫進去 —— 把跳躍目標指到指令中間,
    //    而且完全沒有徵兆。所以寫入前一律逐字比對,不符就整個停用,絕不猜。
    private static readonly byte[] OriginalBytes1 = [0x75, 0x39];
    private static readonly byte[] OriginalBytes2 = [0x74, 0x18];
    private static readonly byte[] PatchedBytes = [0x90, 0x90];

    private readonly Config _config;
    private readonly IPluginLog _log;

    /// <summary>
    ///     patch 是否通過原始位元組驗證。false ＝ 這個 session 一個 byte 都不會寫進遊戲記憶體。
    /// </summary>
    private bool _armed;

    /// <summary>
    ///     我們是否相信 patch 目前套用中。只用來在「還原被拒絕」時誠實回報殘留狀態。
    /// </summary>
    private bool _patchApplied;

    private bool _commandRegistered;

    public SkipCutscene(IPluginLog pluginLog)
    {
        _log = pluginLog;

        if (Interface.GetPluginConfig() is not Config configuration || configuration.Version == 0)
            configuration = new Config { IsEnabled = true, Version = 1 };

        _config = configuration;

        Localization.Init(Interface.AssemblyLocation.DirectoryName);

        // ScanText 找不到時是**擲 KeyNotFoundException**,不是回 IntPtr.Zero
        // (舊寫法的 `!= IntPtr.Zero` 檢查因此是死碼,特徵碼一斷建構子直接爆掉)。
        // 用 TryScanText 才能把「特徵碼斷了」變成一行看得到的 Information。
        if (!SigScanner.TryScanText(Sig1, out var offset1) || !SigScanner.TryScanText(Sig2, out var offset2))
        {
            _log.Information(
                "[SkipCutscene] 找不到過場略過的特徵碼,已停用以保護遊戲——可能是遊戲改版。");
            RegisterCommand();
            return;
        }

        Address = (offset1, offset2);

        var moduleBase = SigScanner.Module.BaseAddress.ToInt64();
        _log.Information(
            "[SkipCutscene] Offset1: [\"ffxiv_dx11.exe\"+{0}]",
            (offset1.ToInt64() - moduleBase).ToString("X"));
        _log.Information(
            "[SkipCutscene] Offset2: [\"ffxiv_dx11.exe\"+{0}]",
            (offset2.ToInt64() - moduleBase).ToString("X"));

        // 🔴 fail-closed:寫任何一個 byte 之前,先確認兩處現場都是我們認得的東西。
        var ok1 = TryReadSite("Offset1", offset1, OriginalBytes1, out var current1);
        var ok2 = TryReadSite("Offset2", offset2, OriginalBytes2, out var current2);
        if (!ok1 || !ok2)
        {
            _log.Information(
                "[SkipCutscene] 目標指令與預期不符,已停用以保護遊戲——可能是遊戲改版。");
            RegisterCommand();
            return;
        }

        _armed = true;

        // 已經是 90 90 = 上一次載入沒還原乾淨(或別的外掛也 patch 了同一處)。
        // 這不是錯誤,只是「已生效」,不要重複寫也不要誤報。
        _patchApplied = BytesEqual(current1, PatchedBytes) && BytesEqual(current2, PatchedBytes);

        _log.Information(
            "[SkipCutscene] 目標指令與預期相符({0} / {1}),略過過場功能可用。",
            Hex(current1),
            Hex(current2));

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

        // 還原路徑同樣要驗:SetEnabled(false) 內部會先確認現場是「原始」或「我們寫進去的
        // 90 90」兩者之一,不符就整組放棄 —— 絕不把 75 39 / 74 18 盲寫回去。
        SetEnabled(false);

        if (_patchApplied)
        {
            _log.Information(
                "[SkipCutscene] 還原被拒絕(現場與預期不符),patch 可能仍留在遊戲記憶體中;" +
                "重新啟動遊戲即可完全復原。");
        }

        GC.SuppressFinalize(this);
    }

    public string Name => "SkipCutscene";

    [PluginService] public IDalamudPluginInterface Interface { get; private set; }

    [PluginService] public ISigScanner SigScanner { get; private set; }

    [PluginService] public ICommandManager CommandManager { get; private set; }

    [PluginService] public IChatGui ChatGui { get; private set; }

    public (nint Offset1, nint Offset2) Address = new(nint.Zero, nint.Zero);

    public void SetEnabled(bool isEnable)
    {
        if (!_armed) return;

        // 第一階段:兩處全部驗過才動手。半套 patch(一處改了一處沒改)會留下我們自己
        // 也還原不回去的狀態,所以只要有一處對不上,一個 byte 都不寫。
        var ok1 = TryReadSite("Offset1", Address.Offset1, OriginalBytes1, out var current1);
        var ok2 = TryReadSite("Offset2", Address.Offset2, OriginalBytes2, out var current2);
        if (!ok1 || !ok2)
        {
            _armed = false;
            _log.Information(
                "[SkipCutscene] 目標指令與預期不符,已停用以保護遊戲——可能是遊戲改版。");
            return;
        }

        // 第二階段:只寫真的需要改的位元組。
        var written1 = WriteSite("Offset1", Address.Offset1, current1, isEnable ? PatchedBytes : OriginalBytes1);
        var written2 = WriteSite("Offset2", Address.Offset2, current2, isEnable ? PatchedBytes : OriginalBytes2);

        if (written1 && written2)
            _patchApplied = isEnable;
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
    ///     兩者皆不符 ＝ 不認得的現場,回 false(呼叫端一律停用整個外掛的寫入能力)。
    /// </summary>
    private bool TryReadSite(string label, nint address, byte[] original, out byte[] current)
    {
        current = [];

        if (address == nint.Zero)
        {
            _log.Information("[SkipCutscene] {0} 位址是 0,已停用以保護遊戲。", label);
            return false;
        }

        // ReadProcessMemory 由核心驗證可讀性,位址壞掉是回 false 而不是 AccessViolation。
        if (!SafeMemory.ReadBytes(address, original.Length, out var buffer) || buffer.Length != original.Length)
        {
            _log.Information(
                "[SkipCutscene] {0} 讀取失敗(位址 {1}),已停用以保護遊戲。",
                label,
                address.ToInt64().ToString("X"));
            return false;
        }

        current = buffer;

        if (BytesEqual(buffer, original) || BytesEqual(buffer, PatchedBytes))
            return true;

        _log.Information(
            "[SkipCutscene] {0} 目標指令與預期不符:位址 {1} 現在是 [{2}],預期原始 [{3}] 或已 patch [{4}]。",
            label,
            address.ToInt64().ToString("X"),
            Hex(buffer),
            Hex(original),
            Hex(PatchedBytes));
        return false;
    }

    /// <summary>
    ///     把 <paramref name="desired" /> 寫進去。現場已經是目標值就不重複寫。
    ///     回傳「現場現在確實等於 desired」。
    /// </summary>
    private bool WriteSite(string label, nint address, byte[] current, byte[] desired)
    {
        if (BytesEqual(current, desired))
            return true;

        if (SafeMemory.WriteBytes(address, desired))
            return true;

        _log.Information(
            "[SkipCutscene] {0} 寫入失敗(位址 {1},WriteProcessMemory 回 false)。",
            label,
            address.ToInt64().ToString("X"));
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

        if (!_armed)
        {
            ChatGui.PrintError(
                "Skip Cutscene: disabled to protect the game - the target instructions do not match what this plugin expects (game update?)."
                    .Loc());
            return;
        }

        var desired = !_config.IsEnabled;
        SetEnabled(desired);

        // SetEnabled 可能在這一刻才發現現場對不上而解除武裝 —— 那就不要謊報成功。
        if (!_armed)
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
