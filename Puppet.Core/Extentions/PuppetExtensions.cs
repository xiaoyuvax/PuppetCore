using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Reflection;
using Puppet.Core.Serialization;
using Puppet.Core.Usage;
using Wima.Core;

namespace Puppet.Core.Extentions
{
    /// <summary>
    /// IPuppet 公共扩展方法。
    /// 提供 Describe（能力描述）、GetState（状态查询）、Invoke（方法调用）、Register/Unregister（注册表）。
    /// </summary>
    public static class PuppetExtensions
    {
        /// <summary>
        /// 获取运行时状态值（路径查询为主，可 include/exclude）。
        /// path 示例：$.Text 或 $.controls[?(@.name=='btnStart')] 或 $.scenes[0].name
        /// 语法统一到 JSONPath（RFC 9535），支持有序集合（数组 [i]）和键值对（['key']），任意层级。
        /// 注意：本方法仅返回值，不返回 schema。schema 请用 Describe()。
        /// </summary>
        /// <param name="target">目标实例</param>
        /// <param name="jsonPath">JSONPath 过滤表达式，null=返回完整序列化</param>
        /// <param name="include">白名单字段名</param>
        /// <param name="exclude">黑名单字段名</param>
        public static string GetState(this IPuppet target,
            string jsonPath = null,
            string[] include = null,
            string[] exclude = null)
        {
            // 简单路径优化：$.PropName 或 $.a.b.c 直接导航成员，避免序列化整个对象图。
            // 优势：（1）效率高；（2）规避含不可序列化成员（如 IPAddress.ScopeId）导致的序列化失败；
            // （3）避免跨线程访问 WinForm 控件属性（GetProperty 内置 UI 线程切换）。
            if (!string.IsNullOrEmpty(jsonPath))
            {
                var stripped = jsonPath.StartsWith("$.") ? jsonPath[2..] : jsonPath;
                if (IsSimpleDottedPath(stripped))
                    return target.GetProperty(stripped);
            }

            // 完整序列化路径：需在 UI 线程执行，避免跨线程访问 WinForm 控件导致崩溃
            var settings = new JsonSerializerSettings
            {
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                //rootType：跳过 WinForms/ComponentModel 基类声明的属性——
                //基类属性 getter 有副作用（UIA COM、原生句柄、design-time 对象），
                //实测曾抛 NotSupportedException"参考的对象类型不支持尝试的操作"甚至直接崩溃进程
                ContractResolver = new AgentContractResolver(include, exclude, target.GetType()),
                MaxDepth = 10,
                Formatting = Formatting.None,
                //单属性 getter 异常只跳过该属性，不中断整体序列化（如 Font 在控件释放后 getter 抛 ObjectDisposedException）
                Error = (s, e) => { e.ErrorContext.Handled = true; }
            };

            string json;
            try
            {
                json = (string)RunOnUi(target, () => JsonConvert.SerializeObject(target, settings));
            }
            catch (Exception ex) { return Utils.ToJson(new { ok = false, err = ex.Message }); }

            if (!string.IsNullOrEmpty(jsonPath))
            {
                try
                {
                    var token = JToken.Parse(json).SelectToken(jsonPath);
                    json = token?.ToString(Formatting.None) ?? "null";
                }
                catch (Exception ex)
                {
                    return Utils.ToJson(new { ok = false, err = $"jsonpath error: {ex.Message}" });
                }
            }
            return json;
        }

        /// <summary>
        /// 判断是否为简单点分路径（如 "IsL3dFileLoading"、"tscboSceneID.Text"）。
        /// 简单路径不含数组下标、过滤器、通配符，可直接用 GetProperty 导航，避免全量序列化。
        /// </summary>
        private static bool IsSimpleDottedPath(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var part in s.Split('.'))
            {
                if (part.Length == 0) return false;
                if (!(char.IsLetter(part[0]) || part[0] == '_')) return false;
                for (int i = 1; i < part.Length; i++)
                    if (!(char.IsLetterOrDigit(part[i]) || part[i] == '_')) return false;
            }
            return true;
        }

        /// <summary>
        /// 在目标线程执行 action 并返回结果（UI 线程切换统一入口）。
        /// 切换依据（按优先级）：
        /// 1. target 为 ISynchronizeInvoke 且 InvokeRequired=true → sync.Invoke（句柄已创建，精确判断）
        /// 2. PuppetUiContext.ShouldSwitch()（当前非 UI 线程且已捕获 UI 上下文）→ context.Send
        ///    兜底覆盖控件句柄未创建时 InvokeRequired 恒为 false 的误判
        ///    （如未显示过的窗体在 Web 线程被调用，其方法内初始化 WebView2 需 STA，MTA 下报 RPC_E_CHANGED_MODE）
        /// 3. 否则直接执行（当前线程即目标线程，或无上下文可切换）
        /// </summary>
        /// <param name="syncTarget">同步目标（通常是 IPuppet 或其导航到的子控件）</param>
        /// <param name="action">要执行的闭包</param>
        private static object RunOnUi(object syncTarget, Func<object> action)
        {
            if (syncTarget is System.ComponentModel.ISynchronizeInvoke sync && sync.InvokeRequired)
                return sync.Invoke(action, null);
            if (PuppetUiContext.ShouldSwitch())
                return PuppetUiContext.Send(action);
            return action();
        }

        /// <summary>
        /// 调用公共方法（参数以 object 数组传入，由 Web 端点从 JSON 反序列化）。
        /// 支持 async 方法（Task / Task&lt;T&gt;）：阻塞调用线程等待完成并提取 Task&lt;T&gt;.Result。
        /// 自动跳过 [PuppetIgnore] 标记的方法；支持重载（按参数个数匹配）。
        /// WinForm 控件自动切换到 UI 线程执行：async 方法在 UI 线程启动后其 continuation 回到空闲 UI 线程，无死锁。
        /// </summary>
        /// <param name="target">目标实例</param>
        /// <param name="methodName">方法名</param>
        /// <param name="args">参数数组（已反序列化的对象）</param>
        public static string Invoke(this IPuppet target, string methodName, object[] args)
        {
            var type = target.GetType();
            var method = ResolveMethod(type, methodName, args);

            if (method == null) return Utils.ToJson(new { ok = false, err = "method not found" });
            if (method.GetCustomAttribute<PuppetIgnoreAttribute>() != null)
                return Utils.ToJson(new { ok = false, err = "method not accessible" });

            PuppetUsage.Track($"invoke::{methodName}", $"{target.AgentInstanceName}.{methodName}(args={args?.Length})");

            try
            {
                var parameters = BindParameters(method, args);
                object retVal;

                // WinForm 控件线程安全：在 UI 线程执行方法的同步部分（async 方法返回未完成 Task）
                retVal = RunOnUi(target, () => method.Invoke(target, parameters));

                // async 方法支持：阻塞调用线程等待 Task 完成；方法 continuation 回到空闲 UI 线程执行，无死锁
                var returnType = method.ReturnType;
                if (retVal is System.Threading.Tasks.Task task)
                {
                    task.GetAwaiter().GetResult();
                    if (returnType.IsGenericType)
                    {
                        var resultProp = returnType.GetProperty("Result");
                        retVal = resultProp?.GetValue(retVal);
                    }
                    else
                    {
                        retVal = null; // Task（非泛型）：无返回值
                    }
                }

                return Utils.ToJson(new { ok = true, result = retVal, returnType = method.ReturnType?.FullName });
            }
            catch (Exception ex)
            {
                target.AgentLog?.Error($"[Agent Invoke] {methodName} failed", ex);
                PuppetUsage.Error("invoke", $"{target.AgentInstanceName}.{methodName}", ex);
                var (msg, stack) = ex is System.Reflection.TargetInvocationException tie
                    ? (tie.InnerException?.Message ?? tie.Message, tie.InnerException?.StackTrace ?? tie.StackTrace)
                    : (ex.Message, ex.StackTrace);
                return Utils.ToJson(new { ok = false, err = msg, stack });
            }
        }

        /// <summary>
        /// 解析实例方法（跳过 [PuppetIgnore]）。支持重载：优先按参数个数匹配，仍多个则取首个。
        /// 查找规则：public 方法默认可访问；非 public 方法需带 [PuppetExpose] 特性才可访问。
        /// 这避免了修改成员可访问性修饰符，又保留了 Agent 的测试/调用入口。
        /// </summary>
        private static MethodInfo ResolveMethod(Type type, string methodName, object[] args)
        {
            // 1. 优先 public
            var publicCandidates = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == methodName && m.GetCustomAttribute<PuppetIgnoreAttribute>() == null)
                .ToArray();

            MethodInfo[] candidates;
            if (publicCandidates.Length > 0)
            {
                candidates = publicCandidates;
            }
            else
            {
                // 2. 非 public 需带 [PuppetExpose]（且未标 [PuppetIgnore]）
                candidates = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(m => m.Name == methodName
                        && m.GetCustomAttribute<PuppetExposeAttribute>() != null
                        && m.GetCustomAttribute<PuppetIgnoreAttribute>() == null)
                    .ToArray();
            }

            if (candidates.Length == 0) return null;
            if (candidates.Length == 1) return candidates[0];

            var argCount = args?.Length ?? 0;
            var byCount = candidates.Where(m => m.GetParameters().Length == argCount).ToArray();
            return byCount.Length > 0 ? byCount[0] : candidates[0];
        }

        /// <summary>
        /// 解析实例属性（跳过 [PuppetIgnore]）。
        /// 查找规则：public 属性默认可访问；非 public 属性需带 [PuppetExpose] 特性才可访问。
        /// </summary>
        private static PropertyInfo ResolveProperty(Type type, string propName)
        {
            // 1. public 优先
            var prop = type.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.GetCustomAttribute<PuppetIgnoreAttribute>() == null)
                return prop;

            // 2. 非 public 需带 [PuppetExpose]（且未标 [PuppetIgnore]）
            prop = type.GetProperty(propName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null
                && prop.GetCustomAttribute<PuppetExposeAttribute>() != null
                && prop.GetCustomAttribute<PuppetIgnoreAttribute>() == null)
                return prop;

            return null;
        }

        /// <summary>
        /// 统一成员访问器：包装 PropertyInfo 或 FieldInfo，提供一致的 GetValue/SetValue 接口。
        /// WinForm 设计器生成的控件（如 private ToolStripComboBox tscboSceneID;）是字段而非属性，
        /// 原 ResolveProperty 仅查属性无法访问这些控件。本访问器同时支持属性和字段。
        /// </summary>
        private readonly struct MemberAccessor
        {
            public readonly PropertyInfo Prop;
            public readonly FieldInfo Field;
            public MemberAccessor(PropertyInfo p) { Prop = p; Field = null; }
            public MemberAccessor(FieldInfo f) { Prop = null; Field = f; }
            public Type MemberType => Prop?.PropertyType ?? Field?.FieldType;
            public bool CanRead => Prop?.CanRead ?? Field != null;
            public bool CanWrite => Prop?.CanWrite ?? (Field != null && !Field.IsInitOnly);
            public object GetValue(object obj) => Prop != null ? Prop.GetValue(obj) : Field?.GetValue(obj);
            public void SetValue(object obj, object value)
            {
                if (Prop != null) Prop.SetValue(obj, value);
                else Field?.SetValue(obj, value);
            }
        }

        /// <summary>
        /// 解析实例成员（属性或字段，跳过 [PuppetIgnore]）。
        /// 查找规则：
        ///   - public 属性/字段：默认可访问
        ///   - 非 public 属性：需带 [PuppetExpose]（属性可能有副作用逻辑，需显式授权）
        ///   - 非 public 字段：无需 [PuppetExpose]（WinForm 设计器生成的控件如 tscboSceneID 是 private 字段，
        ///     在 Designer.cs 中自动生成、随设计器变更重新生成，逐个标注不现实；字段是数据/控件引用，
        ///     读写风险低于属性；密钥鉴权是真正的安全边界，[PuppetIgnore] 可显式屏蔽敏感字段）
        /// </summary>
        private static MemberAccessor ResolveMember(Type type, string memberName)
        {
            // 1. public 属性优先
            var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.GetCustomAttribute<PuppetIgnoreAttribute>() == null)
                return new MemberAccessor(prop);

            // 2. public 字段
            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance);
            if (field != null && field.GetCustomAttribute<PuppetIgnoreAttribute>() == null)
                return new MemberAccessor(field);

            // 3. 非 public 属性需带 [PuppetExpose]（且未标 [PuppetIgnore]）
            prop = type.GetProperty(memberName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null
                && prop.GetCustomAttribute<PuppetExposeAttribute>() != null
                && prop.GetCustomAttribute<PuppetIgnoreAttribute>() == null)
                return new MemberAccessor(prop);

            // 4. 非 public 字段：无需 [PuppetExpose]，仅跳过 [PuppetIgnore]（便于访问设计器生成的 private 控件字段）
            field = type.GetField(memberName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null && field.GetCustomAttribute<PuppetIgnoreAttribute>() == null)
                return new MemberAccessor(field);

            return default;
        }

        /// <summary>
        /// 解析路径中的单个段（支持 name[index] 索引语法）。
        /// 支持 IList&lt;T&gt;[int]、IList&lt;T&gt;[string]、Dictionary&lt;string,T&gt;[string]、Array[int]。
        /// </summary>
        private static (object value, Type type, string error) ResolvePathPart(object current, Type type, string part)
        {
            // 检查是否含索引器 name[index]
            var bracketStart = part.IndexOf('[');
            if (bracketStart >= 0 && part.EndsWith(']'))
            {
                var propName = part.Substring(0, bracketStart);
                var indexStr = part.Substring(bracketStart + 1, part.Length - bracketStart - 2);

                // 先解析属性/字段
                if (!string.IsNullOrEmpty(propName))
                {
                    var member = ResolveMember(type, propName);
                    if (!member.IsFound())
                        return (null, null, $"property '{propName}' not found on {type.Name}");
                    current = member.GetValue(current);
                    if (current == null)
                        return (null, null, $"property '{propName}' is null");
                    type = current.GetType();
                }

                // 索引访问
                return ResolveIndex(current, type, indexStr);
            }

            // 普通属性/字段
            var m = ResolveMember(type, part);
            if (!m.IsFound())
                return (null, null, $"property '{part}' not found on {type.Name}");
            current = m.GetValue(current);
            return (current, current?.GetType(), null);
        }

        /// <summary>
        /// 对集合/数组执行索引访问。
        /// 支持：IList&lt;T&gt;[int]、IList&lt;T&gt;[string]、Dictionary&lt;string,T&gt;[string]、Array[int]、
        ///       IList&lt;T&gt;[string]（按 ToString 匹配元素属性）。
        /// </summary>
        private static (object value, Type type, string error) ResolveIndex(object collection, Type type, string indexStr)
        {
            // IList<T>[int]
            if (collection is System.Collections.IList list)
            {
                if (int.TryParse(indexStr, out int intIdx))
                {
                    if (intIdx < 0 || intIdx >= list.Count)
                        return (null, null, $"index {intIdx} out of range (count={list.Count})");
                    var val = list[intIdx];
                    return (val, val?.GetType(), null);
                }
                // string index: try matching element property "id" or "name"
                foreach (var item in list)
                {
                    if (item == null) continue;
                    var idProp = item.GetType().GetProperty("id", BindingFlags.Public | BindingFlags.Instance);
                    if (idProp != null && string.Equals(idProp.GetValue(item)?.ToString(), indexStr, StringComparison.Ordinal))
                        return (item, item.GetType(), null);
                    var nameProp = item.GetType().GetProperty("name", BindingFlags.Public | BindingFlags.Instance);
                    if (nameProp != null && string.Equals(nameProp.GetValue(item)?.ToString(), indexStr, StringComparison.Ordinal))
                        return (item, item.GetType(), null);
                }
                return (null, null, $"no element with id/name '{indexStr}' in list (count={list.Count})");
            }

            // Dictionary<string,T>[string]
            if (collection is System.Collections.IDictionary dict)
            {
                var keyType = dict.GetType().GetGenericArguments().FirstOrDefault();
                if (keyType != null)
                {
                    object key = Convert.ChangeType(indexStr, keyType);
                    if (!dict.Contains(key))
                        return (null, null, $"key '{indexStr}' not found in dictionary");
                    var val = dict[key];
                    return (val, val?.GetType(), null);
                }
            }

            // Array[int]
            if (type.IsArray && collection is Array arr)
            {
                if (int.TryParse(indexStr, out int arrIdx))
                {
                    if (arrIdx < 0 || arrIdx >= arr.Length)
                        return (null, null, $"index {arrIdx} out of range (length={arr.Length})");
                    var val = arr.GetValue(arrIdx);
                    return (val, val?.GetType(), null);
                }
            }

            return (null, null, $"type '{type.Name}' does not support indexing");
        }

        /// <summary>判断成员访问器是否有效（未找到时 Prop 和 Field 均为 null）</summary>
        private static bool IsFound(this in MemberAccessor m) => m.Prop != null || m.Field != null;

        private static object[] BindParameters(MethodInfo method, object[] args)
        {
            var params_ = method.GetParameters();
            var result = new object[params_.Length];
            for (int i = 0; i < params_.Length; i++)
            {
                if (i < args?.Length && args[i] != null)
                {
                    var paramType = params_[i].ParameterType;
                    var arg = args[i];
                    if (arg is JToken jt)
                        result[i] = jt.ToObject(paramType);
                    else if (!paramType.IsAssignableFrom(arg.GetType()))
                        result[i] = Convert.ChangeType(arg, paramType);
                    else
                        result[i] = arg;
                }
                else if (params_[i].IsOptional)
                    result[i] = params_[i].DefaultValue;
                else
                    result[i] = params_[i].ParameterType.IsValueType
                        ? Activator.CreateInstance(params_[i].ParameterType) : null;
            }
            return result;
        }

        /// <summary>
        /// 设置公共属性值，支持点分路径访问子对象属性（如 "txtName.Text"、"subForm.subControl.Checked"）。
        /// 模拟用户操作的核心能力：设置 TextBox.Text / CheckBox.Checked / ComboBox.SelectedIndex 等会触发对应事件，
        /// 与用户在 UI 上的实际操作效果一致。WinForm 控件自动切换到 UI 线程执行。
        /// 自动跳过 [PuppetIgnore] 标记的属性；只读属性返回错误。
        /// </summary>
        /// <param name="target">目标实例</param>
        /// <param name="path">点分属性路径（如 "Text" 或 "txtName.Text"）</param>
        /// <param name="value">要设置的值（JToken 会按目标类型反序列化，基本类型自动转换）</param>
        public static string SetProperty(this IPuppet target, string path, object value)
        {
            if (string.IsNullOrEmpty(path))
                return Utils.ToJson(new { ok = false, err = "path is empty" });

            var parts = path.Split('.');
            object current = target;
            Type type = target.GetType();

            try
            {
                // 导航到倒数第二个对象（支持属性、字段和索引器，如 scenes[0].nodes）
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    var (val, typ, err) = ResolvePathPart(current, type, parts[i]);
                    if (err != null)
                        return Utils.ToJson(new { ok = false, err = err });
                    current = val;
                    if (current == null)
                        return Utils.ToJson(new { ok = false, err = $"property '{parts[i]}' is null" });
                    type = typ;
                }

                var lastPart = parts[^1];
                var lastBracket = lastPart.IndexOf('[');
                if (lastBracket >= 0 && lastPart.EndsWith(']'))
                {
                    // 最后一段含索引器：先导航到集合，再按索引设置值
                    var propName = lastPart.Substring(0, lastBracket);
                    if (!string.IsNullOrEmpty(propName))
                    {
                        var preMember = ResolveMember(type, propName);
                        if (!preMember.IsFound())
                            return Utils.ToJson(new { ok = false, err = $"property '{propName}' not found on {type.Name}" });
                        current = preMember.GetValue(current);
                        if (current == null)
                            return Utils.ToJson(new { ok = false, err = $"property '{propName}' is null" });
                        type = current.GetType();
                    }
                    var indexStr = lastPart.Substring(lastBracket + 1, lastPart.Length - lastBracket - 2);
                    var (indexed, idxType, idxErr) = ResolveIndex(current, type, indexStr);
                    if (idxErr != null)
                        return Utils.ToJson(new { ok = false, err = idxErr });

                    // 类型转换
                    object val = value;
                    if (idxType != null)
                    {
                        if (value is JToken jt2)
                            val = jt2.ToObject(idxType);
                        else if (value != null && !idxType.IsAssignableFrom(value.GetType()))
                            val = Convert.ChangeType(value, idxType);
                    }

                    // 写回索引位置
                    if (current is System.Collections.IList setList && int.TryParse(indexStr, out int setIdx))
                    {
                        RunOnUi(current, () => { setList[setIdx] = val; return null; });
                    }
                    else if (current is System.Collections.IDictionary setDict)
                    {
                        var keyType = setDict.GetType().GetGenericArguments().FirstOrDefault();
                        object key = Convert.ChangeType(indexStr, keyType);
                        RunOnUi(current, () => { setDict[key] = val; return null; });
                    }
                    else
                        return Utils.ToJson(new { ok = false, err = $"type '{type.Name}' does not support index setting" });

                    return Utils.ToJson(new { ok = true, path = path, value = val, propType = idxType?.FullName });
                }
                else
                {
                    // 普通属性/字段
                    var lastMember = ResolveMember(type, lastPart);
                    if (!lastMember.IsFound())
                        return Utils.ToJson(new { ok = false, err = $"property '{lastPart}' not found on {type.Name}" });
                    if (!lastMember.CanWrite)
                        return Utils.ToJson(new { ok = false, err = $"property '{lastPart}' is read-only" });

                    // 类型转换
                    object val = value;
                    var propType = lastMember.MemberType;
                    if (value is JToken jt)
                        val = jt.ToObject(propType);
                    else if (value != null && !propType.IsAssignableFrom(value.GetType()))
                        val = Convert.ChangeType(value, propType);

                    // WinForm 控件线程安全：UI 属性必须在 UI 线程设置
                    RunOnUi(current, () => { lastMember.SetValue(current, val); return null; });

                    return Utils.ToJson(new { ok = true, path = path, value = val, propType = propType.FullName });
                }
            }
            catch (Exception ex)
            {
                target.AgentLog?.Error($"[Agent SetProperty] {path} failed", ex);
                var msg = ex is System.Reflection.TargetInvocationException tie
                    ? (tie.InnerException?.Message ?? tie.Message)
                    : ex.Message;
                return Utils.ToJson(new { ok = false, err = msg });
            }
        }

        /// <summary>
        /// 读取公共属性值，支持点分路径访问子对象属性（如 "Text" 或 "txtName.Text"）。
        /// 比 GetState 更直接：无需序列化整个对象图，直接返回单个属性的 JSON 表示。
        /// 自动跳过 [PuppetIgnore] 标记的属性。
        /// </summary>
        /// <param name="target">目标实例</param>
        /// <param name="path">点分属性路径</param>
        public static string GetProperty(this IPuppet target, string path)
        {
            if (string.IsNullOrEmpty(path))
                return Utils.ToJson(new { ok = false, err = "path is empty" });

            var parts = path.Split('.');
            object navResult = null;
            string navError = null;
            string navType = null;

            // 导航函数：支持属性、字段和索引器（如 scenes[0].nodes[3].enabled），需在 UI 线程执行避免跨线程访问
            void DoNavigate()
            {
                object current = target;
                Type type = target.GetType();
                foreach (var part in parts)
                {
                    var (val, typ, err) = ResolvePathPart(current, type, part);
                    if (err != null)
                    {
                        navError = err;
                        return;
                    }
                    current = val;
                    if (current == null)
                    {
                        navResult = null; // 中途 null 是合法的（如未选中项）
                        return;
                    }
                    type = typ;
                }
                navResult = current;
                navType = type.FullName;
            }

            try
            {
                // WinForm 控件线程安全：UI 属性必须在 UI 线程读取
                RunOnUi(target, () => { DoNavigate(); return null; });

                if (navError != null)
                    return Utils.ToJson(new { ok = false, err = navError });

                // null 值直接返回（Agent 可据此区分"未加载"与"已加载"）
                if (navResult == null)
                    return Utils.ToJson(new { ok = true, path = path, value = (object)null, type = navType });

                // 简单类型可直接序列化（基本类型、字符串、枚举、日期、Guid 等），无跨线程风险
                var resultType = navResult.GetType();
                if (IsSimpleType(resultType))
                    return Utils.ToJson(new { ok = true, path = path, value = navResult, type = navType });

                // 复杂对象：不序列化整个对象图。FileMan/Form/WebView2 等对象含跨线程不可访问成员，
                // 在 web 线程序列化会触发跨线程异常或访问已释放原生资源导致进程崩溃。
                // 返回类型信息，Agent 通过更深路径（如 'path.MemberName'）查询具体值。
                return Utils.ToJson(new { ok = true, path = path, value = (object)null, type = navType, note = "complex object; query a deeper path (e.g. '" + path + ".<member>') to inspect specific members" });
            }
            catch (Exception ex)
            {
                target.AgentLog?.Error($"[Agent GetProperty] {path} failed", ex);
                var msg = ex is System.Reflection.TargetInvocationException tie
                    ? (tie.InnerException?.Message ?? tie.Message)
                    : ex.Message;
                return Utils.ToJson(new { ok = false, err = msg });
            }
        }

        /// <summary>
        /// 判断类型是否为"简单类型"——可安全在任意线程序列化，不含 WebView2/Form/事件处理器等危险成员。
        /// 包括：基本类型（int/bool/double 等）、字符串、枚举、decimal、DateTime、TimeSpan、Guid、Uri、char 及其 Nullable 包装。
        /// 复杂对象（如 L3dFileMan/Form）不在此列，GetProperty 对其只返回类型信息不序列化对象图。
        /// </summary>
        private static bool IsSimpleType(Type type)
        {
            if (type == null) return false;
            if (type.IsPrimitive) return true;
            if (type.IsEnum) return true;
            if (type == typeof(string) || type == typeof(decimal) || type == typeof(char)) return true;
            if (type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan)) return true;
            if (type == typeof(Guid) || type == typeof(Uri)) return true;
            var underlying = Nullable.GetUnderlyingType(type);
            return underlying != null && IsSimpleType(underlying);
        }

        /// <summary>注册到 PuppetRegistry（默认公开）</summary>
        public static T Register<T>(this T target) where T : IPuppet
        {
            PuppetRegistry.Register(target);
            return target;
        }

        /// <summary>注册到 PuppetRegistry（带标签，可配置可见性）</summary>
        /// <param name="target">目标实例</param>
        /// <param name="tags">标签数组，用于分类搜索</param>
        /// <param name="internalOnly">true=后台内部实例，默认不返回（除非查询时 includeInternal=true）</param>
        public static T Register<T>(this T target, string[] tags, bool internalOnly = false) where T : IPuppet
        {
            PuppetRegistry.Register(target, tags, internalOnly);
            return target;
        }

        /// <summary>从 PuppetRegistry 注销</summary>
        public static void Unregister(this IPuppet target)
            => PuppetRegistry.Unregister(target.AgentInstanceName ?? target.GetType().Name);
    }
}
