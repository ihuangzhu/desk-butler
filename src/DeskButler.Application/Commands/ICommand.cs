namespace DeskButler.Application.Commands;

/// <summary>定义具有明确响应类型的应用命令。</summary>
/// <typeparam name="TResponse">命令处理完成后返回的响应类型。</typeparam>
public interface ICommand<TResponse>
{
}
