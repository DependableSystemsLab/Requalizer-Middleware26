using System;
using System.Text;
using Microsoft.Extensions.Logging;
using OneOS.Common;

namespace OneOS.Runtime.Driver
{
    public class PythonAgent : ProcessAgent
    {
        private bool _isInteractiveMode;
        private static readonly byte[] NewLineBytes = Encoding.UTF8.GetBytes(Environment.NewLine);

        public PythonAgent(Runtime runtime, string uri, AgentInfo agentInfo, ILogger logger, Agent? parent = null) 
            : base(runtime, uri, agentInfo, logger, parent)
        {
            _logger.LogInformation("Spawned new Python agent {URI} - {FileName} {Arguments}", uri, _processInfo.FileName, string.Join(" ", _processInfo.ArgumentList));
            
            if (_processInfo.FileName.ToLowerInvariant() == "python")
            {
                _processInfo.FileName = "python3";
            }

            if (_processInfo.ArgumentList.Count == 0)
            {
                _isInteractiveMode = true;
                foreach (var flag in new[] { "-i", "-u", "-q" }) _processInfo.ArgumentList.Add(flag);
            }
            else
            {
                _isInteractiveMode = false;
                _processInfo.ArgumentList.Insert(0, "-u");
            }
        }

        protected override void HandleStdin(byte[] payload)
        {
            if (_isInteractiveMode)
            {
                lock (_process.StandardInput)
                {
                    try
                    {
                        if (!_process.HasExited)
                        {
                            _process.StandardInput.BaseStream.Write(payload, 0, payload.Length);
                            _process.StandardInput.BaseStream.Write(NewLineBytes, 0, NewLineBytes.Length);
                            _process.StandardInput.BaseStream.Flush();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to write to stdin of process {PID}.", _process.Id);
                    }
                }
            }
            else
            {
                base.HandleStdin(payload);
            }
        }
        protected override void SetEnvironment()
        {
            base.SetEnvironment();
            _processInfo.Environment["PYTHONUNBUFFERED"] = "1";
        }
    }
}
