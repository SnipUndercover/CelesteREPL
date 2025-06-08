using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Celeste.Mod.Helpers;
using Celeste.Mod.MappingUtils.ImGuiHandlers;
using ImGuiNET;
using JetBrains.Annotations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;

namespace Celeste.Mod.CelesteRepl.Repl;

public partial class CSharpRepl : Tab
{
    private static readonly IReadOnlyList<Assembly> ScriptAssemblyReferences = [
        FakeAssembly.GetFakeEntryAssembly(),
        typeof(FMOD.System).Assembly,
        typeof(On.Celeste.Celeste).Assembly,
        typeof(MonoMod.RuntimeDetour.Hook).Assembly,
        typeof(MonoMod.Utils.DynamicData).Assembly,
        typeof(MonoMod.ILHelpers).Assembly,
        typeof(MonoMod.Backports.MethodImplOptionsEx).Assembly,
        typeof(MonoMod.Core.DetourFactory).Assembly,
        typeof(Mono.Cecil.Cil.OpCodes).Assembly,
    ];

    public bool Initialized => CSharpReplScriptState is not null;

    private Script? CSharpReplScript;
    private ScriptState? CSharpReplScriptState;

    private const string HelpText =
        """
        Welcome to the C# REPL!
        
        You can enter C# code in the text field above. Pressing Enter creates a new line. Ctrl+Enter executes the code.
        Ctrl+Up/Down allows you to go back and forth in submission history. By default, 100 past snippets are saved.
        The amount can be changed in settings.

        The field below is the evaluation history. It shows past script executions and their return values.
        If the snippet failed to compile or threw an exception, it will be logged as well.

        The REPL also supports "Magic Commands". They perform non-standard actions, like clearing the submission history,
          resetting the REPL state, and more.
        Enter #commands for a list of all commands.

        Several helper functions are available in the static "Repl" class, included in the bootloader script.
        To see them, run #bootloader.
        
        If you need to see this text again, run #help.
        """;

    public readonly List<ReplSubmission> Submissions = [
        new("Hello, world!", HelpText, ReplSubmission.State.Success),
    ];

    private string ScriptText = "";
    private int HistoryIndex = -1;

    /// <summary>
    ///   Clear the script text.
    /// </summary>
    public void ClearScriptText()
        => ScriptText = "";

    /// <summary>
    ///   Clear the output history.
    /// </summary>
    public void ClearOutputText()
        => Submissions.Clear();

    private void AddScriptTextToHistory()
    {
        CelesteReplSettings settings = CelesteReplModule.Settings;

        if (settings.CSharpScriptHistory.Count > settings.HistorySize)
        {
            int lastIndex = settings.HistorySize - 1;
            settings.CSharpScriptHistory.RemoveRange(
                lastIndex,
                settings.CSharpScriptHistory.Count - lastIndex);
        }

        settings.CSharpScriptHistory.Insert(0, ScriptText);
    }

    /// <summary>
    ///   Adds a new REPL submission to the logs.
    /// </summary>
    /// <param name="input">
    ///   The input, usually the script text.
    /// </param>
    public void LogInput(string input)
    {
        Submissions.Insert(0, new ReplSubmission(input));
    }

    /// <summary>
    ///   Adds output to the most recent REPL submission.
    /// </summary>
    /// <param name="output">
    ///   The output given by the current submission.
    /// </param>
    /// <param name="state">
    ///   The new submission state. Defaults to <see cref="ReplSubmission.State.Success" />.
    /// </param>
    public void LogOutput(string output, ReplSubmission.State state = ReplSubmission.State.Success)
    {
        if (Submissions.Count == 0)
            return;

        ReplSubmission submission = Submissions[0];
        submission.AddResponse(output);
        submission.ResponseState = state;
    }

    /// <summary>
    ///   Adds an exception to the most recent REPL submission.
    /// </summary>
    /// <param name="output">
    ///   The exception given by the current submission.
    /// </param>
    /// <param name="state">
    ///   The new submission state. Defaults to <see cref="ReplSubmission.State.Error" />.
    /// </param>
    public void LogOutput(Exception output, ReplSubmission.State state = ReplSubmission.State.Error)
    {
        if (Submissions.Count == 0)
            return;

        ReplSubmission submission = Submissions[0];
        submission.AddResponse(output);
        submission.ResponseState = state;
    }

    private const RegexOptions PatternOptions
        = RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"^\s*#(?<command>[a-z]+)\s*(?<args>.*)$", PatternOptions | RegexOptions.Singleline)]
    private static partial Regex GenerateMagicActionInvocationRegex();

    private static readonly Regex MagicActionInvocationRegex = GenerateMagicActionInvocationRegex();

    [GeneratedRegex("^[a-z]+$", PatternOptions)]
    private static partial Regex GenerateMagicActionRegex();

    private static readonly Regex MagicActionRegex = GenerateMagicActionRegex();

    [LanguageInjection("C#")]
    private const string Bootloader =
        """
        public static class Repl
        {
            // REPL instance
            public static Celeste.Mod.CelesteRepl.Repl.CSharpRepl Instance
                => Celeste.Mod.CelesteRepl.CelesteReplModule.Instance.CSharpReplTab;
            
            // Entity helpers
            public static Player? GetPlayer()
                => Engine.Scene.Tracker.GetEntity<Player>();
            
            public static T? GetEntity<T>() where T : Entity
                => Engine.Scene.Tracker.IsEntityTracked<T>()
                    ? Engine.Scene.Tracker.GetEntity<T>()
                    : Engine.Scene.Entities.FindFirst<T>();
                
            public static List<T> GetEntities<T>() where T : Entity
                => Engine.Scene.Tracker.IsEntityTracked<T>()
                    ? (Engine.Scene.Tracker.GetEntities<T>() as List<T>)!
                    : Engine.Scene.Entities.FindAll<T>();
                    
            // Scene helpers
            public static T? SceneAs<T>() where T : Scene
                => Engine.Scene as T;
            
            // Logging
            public static void Log(string output)
                => Instance.LogOutput(output, SuccessState);
                
            public static void Log(object output)
                => Instance.LogOutput(output?.ToString() ?? "null", SuccessState);
            
            public static void Log(Exception output)
                => Instance.LogOutput(output, SuccessState);
                
            // Internals
            private static Celeste.Mod.CelesteRepl.Repl.CSharpRepl.ReplSubmission.State SuccessState
                = Celeste.Mod.CelesteRepl.Repl.CSharpRepl.ReplSubmission.State.Success;
            
            private static Celeste.Mod.CelesteRepl.Repl.CSharpRepl.ReplSubmission.State ErrorState
                = Celeste.Mod.CelesteRepl.Repl.CSharpRepl.ReplSubmission.State.Error;
        }
        """;

    private readonly ImGuiInputTextCallback TextInputDelegate;

    internal unsafe CSharpRepl()
    {
        // prevent the delegate from getting GC'd
        TextInputDelegate = TextInputCallback;
    }

    internal void InitializeRepl(bool force = false)
    {
        const string LogId = $"{nameof(CSharpRepl)}/{nameof(InitializeRepl)}";
        if (!force && Initialized)
        {
            Logger.Warn(LogId, "Attempted to initialize the REPL while it is already initialized.");
            return;
        }

        Logger.Info(LogId, "Initializing REPL, this may take a while.");

        ScriptOptions options = ScriptOptions.Default
            .AddReferences(ScriptAssemblyReferences);

        // it fucking works.
        InteractiveAssemblyLoader iAsmLoader = new();

        foreach (EverestModule mod in Everest.Modules)
        {
            if (mod.Metadata?.DLL is null)
                continue;

            string cachedAsmPath = Everest.Relinker.GetCachedPath(
                mod.Metadata, Path.GetFileNameWithoutExtension(mod.Metadata.DLL));

            iAsmLoader.RegisterDependency(mod.GetType().Assembly);
            options = options.AddReferences(MetadataReference.CreateFromStream(File.OpenRead(cachedAsmPath)));
        }

        options = options.AddImports(
            // system libs
            "System", "System.Collections.Generic", "System.Collections", "System.Linq",
            "System.Text", "System.IO", "System.Reflection", "System.Globalization",

            // celeste libs
            "Celeste", "Monocle", "Celeste.Mod", "Microsoft.Xna.Framework",

            // fmod
            "FMOD", "FMOD.Studio",

            // monomod
            "MonoMod", "MonoMod.RuntimeDetour", "MonoMod.Patcher", "MonoMod.Utils", "MonoMod.Cil");

        CSharpReplScript = CSharpScript.Create(Bootloader, options, null, iAsmLoader);
        ResetReplState();
    }

    private void ResetReplState()
    {
        CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
        try
        {
            CSharpReplScriptState = CSharpReplScript!.RunAsync(null, cts.Token).Result;
        }
        catch (Exception e)
        {
            const string LogId = $"{nameof(CSharpRepl)}/{nameof(ResetReplState)}";

            Logger.Error(LogId, "The REPL state could not be reset.");
            Logger.LogDetailed(e, LogId);
            throw;
        }
    }

    #region ImGui Handler

    private const ImGuiInputTextFlags InputTextFlags =
        ImGuiInputTextFlags.None
        | ImGuiInputTextFlags.EnterReturnsTrue
        | ImGuiInputTextFlags.NoHorizontalScroll
        | ImGuiInputTextFlags.CallbackCompletion
        | ImGuiInputTextFlags.CallbackHistory;

    private const ImGuiInputTextFlags OutputTextFlags =
        ImGuiInputTextFlags.None
        | ImGuiInputTextFlags.ReadOnly
        | ImGuiInputTextFlags.NoUndoRedo;

    public override string Name => "C# REPL";

    public override bool CanBeVisible() => true;

    private const float DefaultInputTextHeight = 110;
    private float InputTextHeight = DefaultInputTextHeight;
    private bool IsResizingText;
    private float LastMouseY;

    public override void Render(Level? _)
    {
        bool runScript = ImGui.InputTextMultiline(
            " Script",
            ref ScriptText,
            0x10000,
            new Vector2(-1f, InputTextHeight),
            InputTextFlags,
            TextInputDelegate
        );

        // input text field resizing
        ImGui.Selectable("", false, ImGuiSelectableFlags.None, new Vector2(0f, 2f));
        if (ImGui.IsItemClicked())
            IsResizingText = true;
        else if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            IsResizingText = false;

        // ImGui.GetMouseDragDelta() exists, but doing this manually is more responsive
        float currentMouseY = ImGui.GetMousePos().Y;
        if (IsResizingText)
        {
            InputTextHeight += currentMouseY - LastMouseY;
            InputTextHeight = Math.Max(InputTextHeight, 10f);
        }
        LastMouseY = currentMouseY;

        StringBuilder builder = new();
        foreach (ReplSubmission submission in Submissions)
            builder.Append(submission).Append('\n');

        string output = builder.ToString();

        ImGui.InputTextMultiline(
            " Output",
            ref output,
            0x0,
            -Vector2.One,
            OutputTextFlags);

        if (!runScript)
            return;

        ScriptText = ScriptText.Trim();
        LogInput(ScriptText);

        if (MagicActionInvocationRegex.Match(ScriptText) is { Success: true } magicCommandInvocation)
        {
            string command = magicCommandInvocation.Groups["command"].Value;
            string args = magicCommandInvocation.Groups["args"].Value.Trim();
            if (MagicActions.TryGetValue(command, out MagicAction? action))
            {
                try
                {
                    if (action.Run(args))
                    {
                        AddScriptTextToHistory();
                        ClearScriptText();
                        HistoryIndex = -1;
                    }
                }
                catch (Exception e)
                {
                    LogOutput(e);
                }

                return;
            }
        }

        if (CSharpReplScriptState is null)
        {
            LogOutput(
                "The REPL could not be initialized. Enter #initialize to try again.", ReplSubmission.State.Error);

            return;
        }

        try
        {
            Task<ScriptState<object>> t = CSharpReplScriptState.ContinueWithAsync(ScriptText);

            ScriptState newState = t.Result;
            LogOutput(newState.ReturnValue?.ToString() ?? "null");

            AddScriptTextToHistory();
            ClearScriptText();
            CSharpReplScriptState = newState;
            HistoryIndex = -1;
        }
        catch (Exception e)
        {
            LogOutput(e);
        }
    }

    private unsafe int TextInputCallback(ImGuiInputTextCallbackData* data)
    {
        if (data->EventKey == ImGuiKey.Tab)
            OnTab(data);

        else if (ImGui.IsKeyDown(ImGuiKey.ModCtrl))
            OnHistory(
                data, data->EventKey switch {
                    ImGuiKey.UpArrow => HistoryScrollDirection.Up,
                    ImGuiKey.DownArrow => HistoryScrollDirection.Down,
                    _ => throw new ArgumentOutOfRangeException($"Unexpected key: {data->EventKey}"),
                });

        return 0;
    }

    // it's just 4 spaces
    private const string Tab = "    ";

    private unsafe void OnTab(ImGuiInputTextCallbackData* data)
    {
        // TODO: completions
        // rn ill just add 4 spaces when pressing tab

        InsertTextBuffer(data, data->CursorPos, Tab);
    }

    private unsafe void OnHistory(ImGuiInputTextCallbackData* data, HistoryScrollDirection scrollDirection)
    {
        CelesteReplSettings settings = CelesteReplModule.Settings;

        // don't move oob
        if ((scrollDirection == HistoryScrollDirection.Up && HistoryIndex == settings.LastHistoryIndex)
            || (scrollDirection == HistoryScrollDirection.Down && HistoryIndex == -1))
            return;

        HistoryIndex += (int)scrollDirection;

        data->CursorPos = 0;

        if (HistoryIndex == -1)
            ClearTextBuffer(data);
        else
            ReplaceTextBuffer(data, settings.CSharpScriptHistory[HistoryIndex]);
    }

    public static unsafe void DeleteTextBuffer(ImGuiInputTextCallbackData* data, int position, int bytesToDelete)
        => ImGuiNative.ImGuiInputTextCallbackData_DeleteChars(data, position, bytesToDelete);

    public static unsafe void InsertTextBuffer(ImGuiInputTextCallbackData* data, int position, string toInsert)
    {
        // heap corruption here i com3WtB*ZGp,^>zSHg^9IWOQEKC!7@~O7

        Span<byte> insertBytes = stackalloc byte[Encoding.UTF8.GetByteCount(toInsert)];
        Encoding.UTF8.GetBytes(toInsert, insertBytes);

        fixed (byte* insertBytesPtr = insertBytes)
            ImGuiNative.ImGuiInputTextCallbackData_InsertChars(
                data, position, insertBytesPtr, insertBytesPtr + insertBytes.Length);
    }

    public static unsafe void ClearTextBuffer(ImGuiInputTextCallbackData* data)
        => DeleteTextBuffer(data, 0, data->BufTextLen);

    public static unsafe void ReplaceTextBuffer(ImGuiInputTextCallbackData* data, string newText)
    {
        ClearTextBuffer(data);
        InsertTextBuffer(data, 0, newText);
    }

    private enum HistoryScrollDirection
    {
        Down = -1,
        Up = 1,
    }

    #endregion

    #region Magic Actions

    // stuff not implemented by the c# scripting engine, but we do anyway for convenience
    private readonly Dictionary<string, MagicAction> MagicActions = new();

    /// <summary>
    ///   Called when registering magic actions. Use <see cref="RegisterMagicAction"/> to register your own.
    /// </summary>
    public event Action<CSharpRepl> OnMagicActionRegister = _ => { };

    internal void RegisterMagicActions()
    {
        RegisterMagicAction(new HelpMagicAction(this));
        RegisterMagicAction(new CommandListMagicAction(this));
        RegisterMagicAction(new ClearOutputMagicAction(this));
        RegisterMagicAction(new ClearHistoryMagicAction(this));
        RegisterMagicAction(new ResetReplStateMagicAction(this));
        RegisterMagicAction(new InitializeReplStateMagicAction(this));
        RegisterMagicAction(new VerboseMagicAction(this));
        RegisterMagicAction(new BootloaderMagicAction(this));
        OnMagicActionRegister.Invoke(this);
    }

    public void RegisterMagicAction(MagicAction action)
    {
        const string LogId = $"{nameof(CSharpRepl)}/{nameof(RegisterMagicAction)}";

        if (!MagicActionRegex.IsMatch(action.Command))
        {
            Logger.Warn(
                LogId,
                $"Attempted to register magic action {action.GetType().FullName} with command \"{action.Command}\", " +
                $"but it doesn't match the pattern \"{MagicActionRegex}\". Skipping.");

            return;
        }

        if (!MagicActions.TryAdd(action.Command, action))
        {
            Logger.Warn(
                LogId,
                $"Attempted to register magic action {action.GetType().FullName} with command \"{action.Command}\", " +
                $"but one already exists ({MagicActions[action.Command].GetType().FullName}]). Skipping.");

            return;
        }

        Logger.Debug(
            LogId,
            $"Registered magic action {action.GetType().FullName} with command \"{action.Command}\".");
    }

    public abstract class MagicAction(CSharpRepl repl)
    {
        /// <summary>
        ///   The REPL state.
        /// </summary>
        protected readonly CSharpRepl Repl = repl;

        /// <summary>
        ///   The command name.
        /// </summary>
        public abstract string Command { get; }

        /// <summary>
        ///   The command description.
        /// </summary>
        public abstract string Description { get; }

        /// <summary>
        ///   Invoked somewhere, probably.
        /// </summary>
        [Obsolete("This method is currently unused.")]
        public virtual void Setup()
        {
        }

        /// <summary>
        ///   Magic action handler. The REPL state can be found in the <see cref="Repl" /> field.
        /// </summary>
        /// <param name="args">
        ///   Magic action arguments. The arguments need to be parsed manually.
        /// </param>
        /// <returns>
        ///   Whether the command succeeded. An exception can also be thrown.
        /// </returns>
        public abstract bool Run(string args);
    }

    public sealed class HelpMagicAction(CSharpRepl repl) : MagicAction(repl)
    {
        public override string Command => "help";
        public override string Description => "Prints the help.";

        public override bool Run(string _)
        {
            Repl.LogOutput(HelpText);

            return true;
        }
    }

    public sealed class CommandListMagicAction(CSharpRepl repl) : MagicAction(repl)
    {
        public override string Command => "commands";
        public override string Description => "Prints a list of commands.";

        public override bool Run(string _)
        {
            foreach ((string? command, MagicAction? action) in Repl.MagicActions)
            {
                Repl.LogOutput($"#{command}: {action.Description}");
            }

            return true;
        }
    }

    public sealed class ClearOutputMagicAction(CSharpRepl repl) : MagicAction(repl)
    {
        public override string Command => "clear";
        public override string Description => "Clears the console output.";

        public override bool Run(string _)
        {
            Repl.ClearOutputText();
            return true;
        }
    }

    public sealed class ClearHistoryMagicAction(CSharpRepl repl) : MagicAction(repl)
    {
        public override string Command => "clearhistory";
        public override string Description => "Clears the command history.";

        public override bool Run(string args)
        {
            if (args.ToLowerInvariant() != "confirm")
            {
                Repl.LogOutput(
                    $"""
                     #{Command} will clear your entire command history.
                     
                     Are you sure? Enter "#{Command} confirm" to confirm.
                     """);
                return false;
            }

            CelesteReplModule.Settings.CSharpScriptHistory.Clear();
            Repl.LogOutput("History cleared.");
            return true;
        }
    }

    public sealed class ResetReplStateMagicAction(CSharpRepl repl) : MagicAction(repl)
    {
        public override string Command => "reset";
        public override string Description => "Resets the REPL state.";

        public override bool Run(string args)
        {
            if (args.ToLowerInvariant() != "confirm")
            {
                Repl.LogOutput(
                    $"""
                    #{Command} resets all REPL definitions, like variables, classes and methods. However, this does not
                    undo the changes done by the REPL. Ensure no entities, hooks, or similar are active before
                    running this command.
                    
                    Are you sure? Enter "#{Command} confirm" to confirm.
                    """);
                return false;
            }

            try
            {
                Repl.ResetReplState();
            }
            catch (Exception e)
            {
                Repl.LogOutput("The REPL state could not be reset.");
                Repl.LogOutput(e);
                return false;
            }

            Repl.ClearOutputText();
            Repl.LogInput($"#{Command} {args}");
            Repl.LogOutput("REPL state reset successfully.");
            return true;
        }
    }

    public sealed class InitializeReplStateMagicAction(CSharpRepl repl) : MagicAction(repl)
    {
        public override string Command => "reinitialize";
        public override string Description => "Re-initializes the REPL.";

        public override bool Run(string args)
        {
            if (args.ToLowerInvariant() != "confirm")
            {
                Repl.LogOutput(
                    $"""
                    #{Command} re-initializes the entire REPL from scratch, which, among other things,
                    reloads all mod assemblies. Useful if you've triggered code reload and want the REPL to
                    notice the changes.
                    Note: Re-initializing the REPL may take a while depending on the number of mods,
                    and may cause deadlocks. Proceed only when ready.
                     
                    Are you sure? Enter "#{Command} confirm" to confirm.
                    """);
                return false;
            }

            try
            {
                Repl.InitializeRepl(force: true);
            }
            catch (Exception e)
            {
                Repl.LogOutput("The REPL could not be re-initialized.");
                Repl.LogOutput(e);
                return false;
            }

            Repl.ClearOutputText();
            Repl.LogInput($"#{Command} {args}");
            Repl.LogOutput("REPL re-initialized successfully.");
            return true;
        }
    }

    public sealed class VerboseMagicAction(CSharpRepl repl) : MagicAction(repl)
    {
        public override string Command => "verbose";
        public override string Description => "Enables verbose stacktraces.";

        public override bool Run(string args)
        {
            CelesteReplSettings settings = CelesteReplModule.Settings;

            switch (args.ToLowerInvariant())
            {
                case "":
                    Repl.LogOutput(
                        $"Verbose stacktraces are currently {(settings.EnableStacktrace ? "enabled" : "disabled")}.");

                    Repl.LogOutput(
                        $"Run #{Command} enable/disable to change the state.");

                    break;
                case "enable":
                    settings.EnableStacktrace = true;
                    Repl.LogOutput("Verbose stacktraces enabled.");
                    break;
                case "disable":
                    settings.EnableStacktrace = false;
                    Repl.LogOutput("Verbose stacktraces disabled.");
                    break;
                default:
                    Repl.LogOutput($"Unknown argument \"{args}\".", ReplSubmission.State.Error);
                    return false;
            }

            return true;
        }
    }

    public sealed class BootloaderMagicAction(CSharpRepl repl) : MagicAction(repl)
    {
        public override string Command => "bootloader";
        public override string Description => "Shows the bootloader script, which contains the \"Repl\" global class.";

        public override bool Run(string _)
        {
            Repl.LogOutput(Bootloader);
            return true;
        }
    }

    #endregion

    #region REPL Submission

    public class ReplSubmission
    {
        public readonly List<string> SubmissionLines;
        public readonly List<string> ResponseLines;

        private State _ResponseState;

        public State ResponseState
        {
            get => _ResponseState;
            set
            {
                if (_ResponseState == value)
                    return;

                _ResponseState = value;
                InvalidateCachedString();
            }
        }

        private string? CachedString;

        public ReplSubmission(string? submission, State responseState = State.Success)
        {
            SubmissionLines = [];
            ResponseLines = [];
            _ResponseState = responseState;

            if (submission is not null)
                SubmissionLines.AddRange(submission.Split("\n"));
        }

        public ReplSubmission(string? submission, string response, State responseState)
            : this(submission, responseState)
        {
            AddResponse(response);
        }

        public ReplSubmission(string? submission, Exception response, State responseState)
            : this(submission, responseState)
        {
            AddResponse(response);
        }

        public void AddResponse(string response)
        {
            ResponseLines.AddRange(response.Split("\n"));
            InvalidateCachedString();
        }

        public void AddResponse(Exception response)
        {
            if (CelesteReplModule.Settings.EnableStacktrace)
                ResponseLines.AddRange(response.ToString().Split("\n"));
            else
                PrepareBriefError(response);

            InvalidateCachedString();
        }

        private void PrepareBriefError(Exception error, int indentCount = 0)
        {
            for (Exception? current = error; current is not null; current = current.InnerException)
            {
                StringBuilder builder = new();

                for (int i = 0; i < indentCount; i++)
                    builder.Append(' ').Append(' ');

                builder
                    .Append('-').Append(' ')
                    .Append(error.GetType().FullName)
                    .Append(':').Append(' ')
                    .Append(error.Message);

                ResponseLines.Add(builder.ToString());

                if (current is not AggregateException aggregate)
                    continue;

                foreach (Exception? child in aggregate.InnerExceptions)
                    PrepareBriefError(child, indentCount + 1);

                break;
            }
        }

        public override string ToString()
        {
            if (CachedString is not null)
                return CachedString;

            StringBuilder builder = new();

            foreach (string line in SubmissionLines)
                builder.Append('<').Append(' ').AppendLine(line);

            char responseChar = ResponseState switch {
                State.Success => '>',
                State.Error => '!',
                _ => throw new ArgumentException($"Invalid {nameof(ReplSubmission)}.{nameof(State)}: {ResponseState}"),
            };

            foreach (string line in ResponseLines)
                builder.Append(responseChar).Append(' ').AppendLine(line);

            return CachedString = builder.ToString();
        }

        public void InvalidateCachedString()
            => CachedString = null;

        public enum State
        {
            Success,
            Error,
        }
    }

    #endregion
}
