using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using Celeste.Mod.CelesteRepl.Repl;
using Celeste.Mod.MappingUtils.ImGuiHandlers;
using Microsoft.CodeAnalysis.Scripting.Hosting;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;

namespace Celeste.Mod.CelesteRepl;

public class CelesteReplModule : EverestModule
{
    public static CelesteReplModule Instance { get; private set; } = default!;

    public override Type SettingsType => typeof(CelesteReplSettings);
    public static CelesteReplSettings Settings => (CelesteReplSettings) Instance._Settings;

    public CelesteReplModule() {
        Instance = this;
#if DEBUG
        // debug builds use verbose logging
        Logger.SetLogLevel(nameof(CelesteReplModule), LogLevel.Verbose);
#else
        // release builds use info logging to reduce spam in log files
        Logger.SetLogLevel(nameof(CelesteReplModule), LogLevel.Info);
#endif
    }

    // idk how to check whether my mod is loaded on boot or hot reloaded, so :shrug:
    private static readonly FieldInfo f_Everest_Initialized =
        typeof(Everest).GetField("_Initialized", BindingFlags.NonPublic | BindingFlags.Static)!;

    public CSharpRepl CSharpReplTab { get; private set; } = default!;

    // roslyn alcs are non-collectible which means they're leaking assemblies all over the place (catresort)
    // time to hook a microsoft assembly!!!!!!!! we being cursed AND criminals!!!
    private static ILHook? ILHook_CoreAssemblyLoaderImpl_LoadContext_Ctor;

    public override void Load()
    {
        // banger reflection incoming
        ILHook_CoreAssemblyLoaderImpl_LoadContext_Ctor = new ILHook(
            typeof(InteractiveAssemblyLoader)
                .Assembly
                .GetType("Microsoft.CodeAnalysis.Scripting.Hosting.CoreAssemblyLoaderImpl")!
                .GetNestedType("LoadContext", BindingFlags.NonPublic)!
                .GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, [
                    typeof(InteractiveAssemblyLoader),
                    typeof(string),
                ])!,
            CoreAssemblyLoaderImpl_LoadContext_ctor
        );
        MainMappingUtils.Tabs.Add(CSharpReplTab = new CSharpRepl());

        Everest.Events.Celeste.OnExiting += SaveSettings;

        // load immediately if we're hot reloaded
        if ((bool)f_Everest_Initialized.GetValue(null)!)
            CSharpReplTab.InitializeRepl();
    }

    public override void Unload()
    {
        ILHook_CoreAssemblyLoaderImpl_LoadContext_Ctor?.Dispose();
        MainMappingUtils.Tabs.Remove(CSharpReplTab);

        Everest.Events.Celeste.OnExiting -= SaveSettings;

        // make sure to save our history
        SaveSettings();
    }

    private static void CoreAssemblyLoaderImpl_LoadContext_ctor(ILContext il)
    {
        ILCursor cursor = new(il);
        if (!cursor.TryGotoNext(MoveType.After,
            static instr => instr.MatchLdarg0(),
            static instr => instr.MatchCall<AssemblyLoadContext>(".ctor")))
        {
            Logger.Error(nameof(CelesteReplModule),
                "whomst the FUCK is IL hooking the CoreAssemblyLoaderImpl+LoadContext class!!!!!! " +
                "we're both criminals so let me do my crimes in peace");
            Logger.Error(nameof(CelesteReplModule),
                "(cannot find base ctor call in CoreAssemblyLoaderImpl+LoadContext..ctor)");
            return;
        }

        cursor.Index--;
        cursor.EmitLdcI4(1);
        cursor.Next!.Operand = il.Import(typeof(AssemblyLoadContext).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, [ typeof(bool) ])!
        );
    }

    public override bool ParseArg(string arg, Queue<string> args)
    {
        // this is only called on boot after all mods' Load(), but before Initialize()
        // if we do stuff in initialize, we hit a deadlock - promise not to call the cops
        if (!CSharpReplTab.Initialized)
            // don't spam the logs lol
            CSharpReplTab.InitializeRepl();

        return false;
    }

    public override void Initialize()
    {
        base.Initialize();
        CSharpReplTab.RegisterMagicActions();
    }
}
