/*
 * Copyright (c) 2026 nstechbytes
 *
 * Licensed under the MIT License.
 * You may obtain a copy of the License at:
 * https://opensource.org/licenses/MIT
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Concurrent;
using Rainmeter;

namespace PluginScreenshot
{
    // Queues Rainmeter bangs for execution on the plugin Update thread.
    // RmExecute is not safe from worker / STA / hotkey threads.
    internal static class BangQueue
    {
        private struct Item
        {
            public API Api;
            public string Command;
        }

        private static readonly ConcurrentQueue<Item> Queue = new ConcurrentQueue<Item>();

        public static void Enqueue(API api, string command)
        {
            if (api == null || string.IsNullOrWhiteSpace(command))
                return;
            Queue.Enqueue(new Item { Api = api, Command = command });
        }

        // Call only from Plugin.Update (Rainmeter main / measure thread).
        public static void Flush()
        {
            while (Queue.TryDequeue(out Item item))
            {
                try
                {
                    item.Api.Execute(item.Command);
                }
                catch (Exception ex)
                {
                    Logger.Log("BangQueue.Flush: " + ex.Message);
                }
            }
        }
    }
}
