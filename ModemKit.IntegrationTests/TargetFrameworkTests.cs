using System.Reflection;
using System.Runtime.Versioning;
using Jmodem.Protocol;
using LegacyModem.Protocol;
using Xmodem.Protocol;
using Ymodem.Protocol;
using Zmodem.Protocol;

namespace ModemKit.IntegrationTests
{
    public sealed class TargetFrameworkTests
    {
        [Fact]
        public void LoadsTheRequestedProtocolAssets()
        {
            var expected = typeof(TargetFrameworkTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "ExpectedProtocolTargetFramework").Value;
            Assert.False(string.IsNullOrEmpty(expected));
            foreach (var assembly in new[] { typeof(XModemSender).Assembly, typeof(YModemSender).Assembly, typeof(ZModemSender).Assembly, typeof(LegacyModemOptions).Assembly, typeof(JModemOptions).Assembly })
            {
                Assert.Equal(expected, assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName);
            }
        }
    }
}
