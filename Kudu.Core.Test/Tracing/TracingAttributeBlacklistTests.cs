using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Kudu.Contracts.Tracing;
using Kudu.Core.Tracing;
using Xunit;

namespace Kudu.Core.Test.Tracing
{
    public class TracingAttributeBlacklistTests
    {
        [Fact]
        public void AttributeBlacklist()
        {
            Assert.True(TraceExtensions.IsNonDisplayableAttribute(TraceExtensions.AlwaysTrace));
            Assert.True(TraceExtensions.IsNonDisplayableAttribute(TraceExtensions.TraceLevelKey));
            Assert.True(TraceExtensions.IsNonDisplayableAttribute("Max-Forwards"));
            Assert.True(TraceExtensions.IsNonDisplayableAttribute("X-ARR-LOG-ID"));
            Assert.False(TraceExtensions.IsNonDisplayableAttribute("url"));
            Assert.False(TraceExtensions.IsNonDisplayableAttribute("method"));
            Assert.False(TraceExtensions.IsNonDisplayableAttribute("type"));
            Assert.False(TraceExtensions.IsNonDisplayableAttribute("Host"));
        }
    }
}
