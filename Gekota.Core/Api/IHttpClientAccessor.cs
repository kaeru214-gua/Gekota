using System.Net.Http;
using System.Threading.Tasks;

namespace Gekota.Core.Api
{
    public interface ICookieTester
    {
        Task<(bool, string)> TestCookieAsync();
    }
}
