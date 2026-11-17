using System;
using System.Linq;
using Puppet.Core;
using Xunit;

namespace Puppet.Core.Tests
{
    [Collection("agent-book")]
    public class AgentBookTests
    {
        [Fact]
        public void Leave_keeps_only_the_farewell_and_drops_registration()
        {
            string id = "T-" + Guid.NewGuid().ToString("N");
            AgentBook.Heartbeat(id, "test", "first", 0, "unit", message: "hello");
            AgentBook.Heartbeat(id, "test", "second", 0, "unit", message: "working");
            Assert.Equal(2, AgentBook.GetChat(from: id).Count);

            AgentBook.Leave(id, "bye");

            var chat = AgentBook.GetChat(from: id);
            Assert.Single(chat);
            Assert.Equal("bye", chat[0].Text);
            Assert.DoesNotContain(AgentBook.GetAll(includeStale: true), a => a.Id == id);
        }

        [Fact]
        public void Heartbeat_keeps_only_the_latest_tool_steps()
        {
            string id = "T-" + Guid.NewGuid().ToString("N");
            for (int i = 1; i <= 15; i++)
                AgentBook.Heartbeat(id, "worker 会话 · D:\\w", "doing", 0, "unit", step: "step" + i);

            var info = AgentBook.GetAll(includeStale: true).Single(a => a.Id == id);
            Assert.Equal(12, info.Steps.Count);
            Assert.EndsWith(" step4", info.Steps[0]);
            Assert.EndsWith(" step15", info.Steps[^1]);
            Assert.Matches(@"^\d{2}:\d{2}:\d{2} step4$", info.Steps[0]);
            AgentBook.Release(id);
        }

        [Fact]
        public void Prune_drops_messages_and_registrations_older_than_retention()
        {
            string id = "T-" + Guid.NewGuid().ToString("N");
            AgentBook.Heartbeat(id, "worker 会话 · D:\\w", "doing", 0, "unit", message: "hello");

            AgentBook.Prune(DateTime.Now + AgentBook.Retention + TimeSpan.FromMinutes(1));

            Assert.Empty(AgentBook.GetChat(from: id));
            Assert.DoesNotContain(AgentBook.GetAll(includeStale: true), a => a.Id == id);
        }
    }
}
