"""Launch a process into the child (desktop-clone) session.

Ported from BetterGenshinImpact `Service/ChildSession/ChildSessionProcessLauncher.cs`.
A process cannot cross into another Windows session by a plain CreateProcess, so
this registers a temporary Task Scheduler task whose principal is the current
interactive user running at Highest, then starts it with `RunEx` and the
TASK_RUN_USE_SESSION_ID flag pointing at the child session. The task is deleted
again in `finally` so it never persists.
"""

import os
import sys
import uuid

from ok import Logger

from src.child_session import wts_api

logger = Logger.get_logger(__name__)

# Task Scheduler COM enumerations (taskschd.h).
_TASK_ACTION_EXECUTE = 0
_TASK_CREATE = 2
_TASK_LOGON_INTERACTIVE_TOKEN = 3
_TASK_RUNLEVEL_HIGHEST = 1
_TASK_RUN_USE_SESSION_ID = 0x4


class SessionLaunchError(RuntimeError):
    pass


def _current_account_name() -> str:
    import win32api
    import win32con

    return win32api.GetUserNameEx(win32con.NameSamCompatible)


def _scheduler():
    import win32com.client

    scheduler = win32com.client.Dispatch("Schedule.Service")
    scheduler.Connect()
    return scheduler


def launch_in_session(session_id, executable_path, arguments="", working_directory=None):
    """Start `executable_path` inside `session_id` with administrator rights.

    Raises SessionLaunchError on any failure. Blocking; call from a worker thread.
    """
    full_path = os.path.abspath(executable_path)
    if not os.path.isfile(full_path):
        raise SessionLaunchError(f"要启动的程序不存在：{full_path}")
    if not full_path.lower().endswith(".exe"):
        raise SessionLaunchError("当前只允许选择 .exe 程序。")

    if working_directory is None:
        working_directory = os.path.dirname(full_path)

    actual_session = wts_api.get_child_session_id()
    if actual_session != session_id:
        raise SessionLaunchError(
            f"目标 Child Session 已发生变化。请求会话为 {session_id}，"
            f"当前会话为 {actual_session if actual_session is not None else '无'}。")

    account = _current_account_name()
    task_name = f"ok-ww-ChildSession-ElevatedLaunch-{uuid.uuid4().hex}"

    scheduler = _scheduler()
    root_folder = scheduler.GetFolder("\\")
    task_definition = scheduler.NewTask(0)
    registered = False
    try:
        task_definition.RegistrationInfo.Author = "ok-ww"
        task_definition.RegistrationInfo.Description = (
            f"临时启动 {os.path.basename(full_path)} 到 Child Session {session_id}")

        settings = task_definition.Settings
        settings.Enabled = True
        settings.Hidden = True
        settings.AllowDemandStart = True
        settings.DisallowStartIfOnBatteries = False
        settings.StopIfGoingOnBatteries = False
        settings.ExecutionTimeLimit = "PT0S"

        principal = task_definition.Principal
        principal.UserId = account
        principal.LogonType = _TASK_LOGON_INTERACTIVE_TOKEN
        principal.RunLevel = _TASK_RUNLEVEL_HIGHEST

        action = task_definition.Actions.Create(_TASK_ACTION_EXECUTE)
        action.Path = full_path
        action.Arguments = arguments or ""
        action.WorkingDirectory = working_directory

        registered_task = root_folder.RegisterTaskDefinition(
            task_name, task_definition, _TASK_CREATE, account, None,
            _TASK_LOGON_INTERACTIVE_TOKEN, None)
        registered = True

        running = registered_task.RunEx(None, _TASK_RUN_USE_SESSION_ID, int(session_id), "")
        if running is None:
            raise SessionLaunchError("任务计划程序没有返回运行实例。")
    finally:
        if registered:
            try:
                root_folder.DeleteTask(task_name, 0)
            except Exception as ex:  # cleanup must not mask a successful launch
                logger.warning(f"删除临时计划任务失败（不影响已启动的程序）：{ex}")
        del task_definition, root_folder, scheduler


def _okww_entry():
    """Return (executable, arguments_prefix, working_directory) for this app."""
    if getattr(sys, "frozen", False):
        return sys.executable, [], os.path.dirname(sys.executable)

    # The real app entry is main.py at the repo root. `ok` is vendored one level
    # below it (<repo>/ok), so derive the repo root from ok's location instead of
    # sys.argv[0] — under `python -m src.child_session.spike` argv[0] is spike.py,
    # not main.py.
    import ok

    repo_root = os.path.dirname(os.path.dirname(os.path.abspath(ok.__file__)))
    main_script = os.path.join(repo_root, "main.py")
    if not os.path.isfile(main_script):
        main_script = os.path.join(os.getcwd(), "main.py")
    return sys.executable, [main_script], os.path.dirname(main_script)


def launch_okww_in_session(session_id, extra_args=None):
    """Launch a second ok-ww into `session_id` in child-session mode."""
    executable, prefix_args, workdir = _okww_entry()
    args = prefix_args + ["--instance", "childSession"]
    if extra_args:
        args.extend(extra_args)
    arguments = " ".join(_quote(a) for a in args)
    logger.info(f"启动子实例到会话 {session_id}: {executable} {arguments}")
    launch_in_session(session_id, executable, arguments, workdir)


def _quote(argument: str) -> str:
    if not argument or any(c in argument for c in ' "\t'):
        return '"' + argument.replace('"', r'\"') + '"'
    return argument
