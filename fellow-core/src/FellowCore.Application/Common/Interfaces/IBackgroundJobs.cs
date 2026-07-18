using System.Linq.Expressions;

namespace FellowCore.Application.Common.Interfaces;

public interface IBackgroundJobs
{
    void Enqueue<T>(Expression<Func<T, Task>> methodCall);
}
