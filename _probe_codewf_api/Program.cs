using System;
using System.Reflection;
using System.Linq;
using CodeWF.Log.Core;

var logViewType = typeof(CodeWF.LogViewer.Avalonia.LogView);

// 列出所有 NonPublic 实例方法（按字母排序）
Console.WriteLine("=== LogView: All NON-PUBLIC instance METHODS ===");
var methods = logViewType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
    .Where(m => !m.IsSpecialName)
    .OrderBy(m => m.Name)
    .ToList();
foreach (var m in methods) {
    string access = m.IsFamily ? "protected" : m.IsAssembly ? "internal" : m.IsFamilyOrAssembly ? "protected internal" : "private";
    var pars = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
    Console.WriteLine($"  {access,-19} {m.ReturnType.Name,-20} {m.Name}({pars})");
}

// 列出所有 NonPublic 实例字段
Console.WriteLine("\n=== LogView: All NON-PUBLIC instance FIELDS ===");
var fields = logViewType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
    .OrderBy(f => f.Name)
    .ToList();
foreach (var f in fields) {
    string access = f.IsFamily ? "protected" : f.IsAssembly ? "internal" : f.IsFamilyOrAssembly ? "protected internal" : "private";
    Console.WriteLine($"  {access,-19} {f.FieldType.Name,-30} {f.Name}");
}

// 再列出所有构造函数（包括非公开）
Console.WriteLine("\n=== LogView: ALL constructors ===");
foreach (var c in logViewType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
    string access = c.IsPublic ? "public" : c.IsFamily ? "protected" : c.IsAssembly ? "internal" : "private";
    var pars = string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
    Console.WriteLine($"  {access} .ctor({pars})");
}

// 现在分析为什么 GetProperty("UserLogs") 没取到 - 查 Logger 的所有 PUBLIC 属性（不用 BindingFlags，用默认的 public instance）
Console.WriteLine("\n=== Logger (CodeWF.Log.Core) ALL PUBLIC INSTANCE PROPERTIES + METHODS ===");
var loggerType = typeof(Logger);
foreach (var p in loggerType.GetProperties()) {
    var acc = (p.CanRead ? "get; " : "") + (p.CanWrite ? "set; " : "");
    Console.WriteLine($"  instance prop {p.PropertyType.Name} {p.Name} {{ {acc}}}");
}
foreach (var p in loggerType.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly)) {
    var acc = (p.CanRead ? "get; " : "") + (p.CanWrite ? "set; " : "");
    Console.WriteLine($"  static prop {p.PropertyType.Name} {p.Name} {{ {acc}}}");
}

// 也看看 UserLogFeed 类型到底是不是 public
Console.WriteLine("\n=== Resolving UserLogFeed ===");
string[] candidateTypeNames = {
    "CodeWF.Log.Core.UserLogFeed",
    "CodeWF.Log.Core.InternalUserLogFeed",
    "CodeWF.Log.Core.UserFeed",
    "CodeWF.Log.Core.LogFeed",
    "CodeWF.Log.Core.ILogFeed",
    "CodeWF.Log.Core.IUserLogFeed",
    "CodeWF.Log.Core.UserLoggerFeed"
};
var coreAsm = typeof(Logger).Assembly;
foreach (var tn in candidateTypeNames) {
    var t = coreAsm.GetType(tn);
    if (t != null) {
        Console.WriteLine($"  FOUND: {tn}");
        Console.WriteLine($"      IsPublic={t.IsPublic} IsNestedPublic={t.IsNestedPublic} IsVisible={t.IsVisible} IsClass={t.IsClass} IsInterface={t.IsInterface}");
        foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Where(x => !x.IsSpecialName)) {
            var pars = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
            Console.WriteLine($"      public method {m.ReturnType.Name} {m.Name}({pars})");
        }
        foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(x => !x.IsSpecialName)) {
            string access = m.IsFamily ? "protected" : m.IsAssembly ? "internal" : m.IsFamilyOrAssembly ? "protected internal" : "private";
            var pars = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
            Console.WriteLine($"      {access} method {m.ReturnType.Name} {m.Name}({pars})");
        }
    } else {
        Console.WriteLine($"  NOT FOUND: {tn}");
    }
}

// 查找所有 INTERNAL/PUBLIC 类型在 CodeWF.Log.Core 中名字含 Feed
Console.WriteLine("\n=== All types in CodeWF.Log.Core with 'Feed' in name ===");
foreach (var t in coreAsm.GetTypes().Where(t => t.Name.Contains("Feed"))) {
    Console.WriteLine($"  {t.FullName}  IsPublic={t.IsPublic} IsVisible={t.IsVisible} IsClass={t.IsClass} IsInterface={t.IsInterface}");
}

// 也看看 LogView 的 Loaded/AttachedToVisualTree 事件或 OnLoaded 覆写 - 应该在那里订阅 feed
Console.WriteLine("\n=== LogView: overriding virtual methods ===");
var baseType = logViewType.BaseType;
while (baseType != null && baseType != typeof(object)) {
    foreach (var baseMethod in baseType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(m => m.IsVirtual && !m.IsSpecialName)) {
        MethodInfo? overrideM = null;
        try { 
            var paramTypes = baseMethod.GetParameters().Select(p => p.ParameterType).ToArray();
            overrideM = logViewType.GetMethod(baseMethod.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, paramTypes, null);
            if (overrideM != null && overrideM.DeclaringType == logViewType) {
                string access = overrideM.IsPublic ? "public" : overrideM.IsFamily ? "protected" : overrideM.IsAssembly ? "internal" : "private";
                var pars = string.Join(", ", paramTypes.Select(p => p.Name));
                Console.WriteLine($"  {access,-10} override {baseMethod.ReturnType.Name,-20} {baseMethod.Name}({pars})");
            }
        } catch {}
    }
    baseType = baseType.BaseType;
}
