using System.Collections.Generic;

namespace Puppet.Core.AppAgent
{
    /// <summary>
    /// 面向 B 的操作结果（与面向 A 的 {ok, result, err} 形状刻意不同——message 面向最终用户的自然语言）。
    /// 方法返回 bool 时框架自动包装：true → ok + message（取自三层文案/方法名）；false → !ok。
    /// 方法返回 OperationResult 时直接采用（message 由宿主业务代码给出最准确）。
    /// </summary>
    public sealed class OperationResult
    {
        public bool Ok { get; init; }
        public string Message { get; init; }
        public object Data { get; init; }

        public static OperationResult Success(string message = null, object data = null) => new() { Ok = true, Message = message, Data = data };
        public static OperationResult Failure(string message, object data = null) => new() { Ok = false, Message = message, Data = data };

        public static implicit operator OperationResult(bool ok) => new() { Ok = ok };
    }

    /// <summary>
    /// 二进制产物：具名 action 返回（直接返回或内嵌 OperationResult.Data），框架存入 AppAgentAssetStore
    /// 并在响应中回传 /appagent/assets/{id} 下载 URL。内存态模拟磁盘文件下载（visualdatahub 先例内化）。
    /// </summary>
    public sealed class PuppetArtifact
    {
        /// <summary>下载文件名（进入 Content-Disposition；空则用 artifact）</summary>
        public string FileName { get; init; }

        /// <summary>MIME 类型（空则按扩展名推断或 application/octet-stream）</summary>
        public string ContentType { get; init; }

        /// <summary>产物字节内容</summary>
        public byte[] Content { get; init; }

        public PuppetArtifact(string fileName, string contentType, byte[] content)
        {
            FileName = fileName; ContentType = contentType; Content = content;
        }
    }
}
