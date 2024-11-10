using FubarDev.FtpServer.AccountManagement;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace AnyFS.MountProviders.FTP.Auth
{
    public class AllowAnyone : IMembershipProviderAsync
    {
        public Task LogOutAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<MemberValidationResult> ValidateUserAsync(string username, string password, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new MemberValidationResult(MemberValidationStatus.Anonymous));
        }

        public Task<MemberValidationResult> ValidateUserAsync(string username, string password)
        {
            return Task.FromResult(new MemberValidationResult(MemberValidationStatus.Anonymous));
        }
    }
}
