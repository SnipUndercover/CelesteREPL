using System;
using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Celeste.Mod.CelesteRepl;

public class CelesteReplSettings : EverestModuleSettings
{
    [SettingIgnore]
    public List<string> CSharpScriptHistory { get; set; } = [];

    [SettingRange(10, 1000, largeRange: true)]
    public int HistorySize { get; set; } = 100;

    [SettingIgnore]
    public bool EnableStacktrace { get; set; } = false;

    [YamlIgnore]
    [SettingIgnore]
    public int LastHistoryIndex => Math.Min(HistorySize, CSharpScriptHistory.Count) - 1;
}
