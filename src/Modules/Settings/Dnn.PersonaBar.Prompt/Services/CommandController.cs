﻿using DotNetNuke.Web.Api;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Web.Http;
using Dnn.PersonaBar.Library.Attributes;
using Dnn.PersonaBar.Library.Prompt;
using Dnn.PersonaBar.Prompt.Common;
using Dnn.PersonaBar.Prompt.Components;
using Dnn.PersonaBar.Prompt.Components.Models;
using Dnn.PersonaBar.Prompt.Components.Repositories;
using DotNetNuke.Instrumentation;
using DotNetNuke.Services.Exceptions;
using DotNetNuke.Services.Localization;
using DotNetNuke.Services.Log.EventLog;

namespace Dnn.PersonaBar.Prompt.Services
{
    [MenuPermission(MenuName = "Dnn.Prompt")]
    [RequireHost]
    public class CommandController : ControllerBase
    {
        private static readonly ILog Logger = LoggerSource.Instance.GetLogger(typeof(CommandController));

        [HttpGet]
        public HttpResponseMessage List()
        {
            return Request.CreateResponse(HttpStatusCode.OK, CommandRepository.Instance.GetCommands().Values);
        }

        [ValidateAntiForgeryToken]
        [HttpPost]
        public HttpResponseMessage Cmd([FromBody] CommandInputModel command)
        {
            var startTime = DateTime.Now;
            try
            {
                var args = command.Args;
                var isHelpCmd = args.First().ToUpper() == "HELP";
                var isHelpLearn = isHelpCmd && args.Length > 1 && args[1].ToUpper() == "LEARN";
                var isHelpSyntax = isHelpCmd && args.Length > 1 && args[1].ToUpper() == "SYNTAX";
                var cmdName = isHelpCmd ? (args.Length > 1 ? args[1].ToUpper() : "") : args.First().ToUpper();
                if (isHelpCmd && (isHelpSyntax || isHelpLearn))
                {
                    return GetHelp(command, null, isHelpSyntax, isHelpLearn);
                }
                if (isHelpCmd && args.Length == 1)
                    return AddLogAndReturnResponse(null, null, command, startTime,
                        string.Format(Localization.GetString("CommandNotFound", Constants.LocalResourcesFile),
                            cmdName.ToLower()));

                var allCommands = CommandRepository.Instance.GetCommands();
                //If command not found and command contain namespace.
                if (!allCommands.ContainsKey(cmdName) && cmdName.IndexOf('.') == -1)
                {
                    var seek = allCommands.Values.FirstOrDefault(c => c.Name.ToUpper() == cmdName);
                    // if there is a command which matches then we assume the user meant that namespace
                    if (seek != null)
                    {
                        cmdName = $"{seek.NameSpace.ToUpper()}.{cmdName}";
                    }
                }

                // if no command found notify
                if (!allCommands.ContainsKey(cmdName))
                {
                    var sbError = new StringBuilder();
                    var suggestion = Utilities.GetSuggestedCommand(cmdName);
                    sbError.AppendFormat(Localization.GetString("CommandNotFound", Constants.LocalResourcesFile), cmdName.ToLower());
                    if (!string.IsNullOrEmpty(suggestion))
                    {
                        sbError.AppendFormat(Localization.GetString("DidYouMean", Constants.LocalResourcesFile), suggestion);
                    }
                    return AddLogAndReturnResponse(null, null, command, startTime, sbError.ToString());
                }
                var cmdTypeToRun = allCommands[cmdName].CommandType;

                // Instantiate and run the command
                try
                {
                    var cmdObj = (IConsoleCommand)Activator.CreateInstance(cmdTypeToRun);
                    if (isHelpCmd) return GetHelp(command, cmdObj);
                    // set env. data for command use
                    cmdObj.Init(args, PortalSettings, UserInfo, command.CurrentPage);
                    return AddLogAndReturnResponse(cmdObj, cmdTypeToRun, command, startTime);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                    return AddLogAndReturnResponse(null, null, command, startTime, ex.Message);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                return AddLogAndReturnResponse(null, null, command, startTime, ex.Message);
            }
        }

        /// <summary>
        /// Log every command run by a users.
        /// </summary>
        /// <param name="consoleCommand"></param>
        /// <param name="cmdTypeToRun"></param>
        /// <param name="command"></param>
        /// <param name="startTime"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        private HttpResponseMessage AddLogAndReturnResponse(IConsoleCommand consoleCommand, Type cmdTypeToRun, CommandInputModel command,
            DateTime startTime, string error = null)
        {
            HttpResponseMessage message;
            var isValid = consoleCommand?.IsValid() ?? false;
            var logInfo = new LogInfo
            {
                LogTypeKey = "PROMPT_ALERT"
            };
            logInfo.LogProperties.Add(new LogDetailInfo("Command", command.CmdLine));
            logInfo.LogProperties.Add(new LogDetailInfo("IsValid", isValid.ToString()));

            try
            {
                if (cmdTypeToRun != null)
                    logInfo.LogProperties.Add(new LogDetailInfo("TypeFullName", cmdTypeToRun.FullName));
                if (isValid)
                {
                    var result = consoleCommand.Run();
                    message = Request.CreateResponse(HttpStatusCode.OK, result);
                    logInfo.LogProperties.Add(new LogDetailInfo("RecordsAffected", result.Records.ToString()));
                    logInfo.LogProperties.Add(new LogDetailInfo("Output", result.Output));
                }
                else
                {
                    logInfo.LogProperties.Add(new LogDetailInfo("Output", consoleCommand?.ValidationMessage ?? error));
                    message = BadRequestResponse(consoleCommand?.ValidationMessage ?? error);
                }
            }
            catch (Exception ex)
            {
                logInfo.Exception = new ExceptionInfo(ex);
                message = BadRequestResponse(ex.Message);
            }
            logInfo.LogProperties.Add(new LogDetailInfo("ExecutionTime(hh:mm:ss)", TimeSpan.FromMilliseconds(DateTime.Now.Subtract(startTime).TotalMilliseconds).ToString(@"hh\:mm\:ss\.ffffff")));
            LogController.Instance.AddLog(logInfo);
            return message;
        }

        private HttpResponseMessage GetHelp(CommandInputModel command, IConsoleCommand consoleCommand, bool showSyntax = false, bool showLearn = false)
        {
            return Request.CreateResponse(HttpStatusCode.OK, CommandRepository.Instance.GetCommandHelp(command, consoleCommand, showSyntax, showLearn));
        }
    }
}