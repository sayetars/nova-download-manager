#include <flutter/dart_project.h>
#include <flutter/flutter_view_controller.h>
#include <windows.h>

#include "flutter_window.h"
#include "utils.h"

int APIENTRY wWinMain(_In_ HINSTANCE instance, _In_opt_ HINSTANCE prev,
                      _In_ wchar_t *command_line, _In_ int show_command) {
  // Attach to console when present (e.g., 'flutter run') or create a
  // new console when running with a debugger.
  if (!::AttachConsole(ATTACH_PARENT_PROCESS) && ::IsDebuggerPresent()) {
    CreateAndAttachConsole();
  }

  // Initialize COM, so that it is available for use in the library and/or
  // plugins. v1.8.0: OleInitialize (superset of STA CoInitializeEx) so the OLE
  // drag source ("drag a downloaded file out to Explorer", IDM parity) works.
  ::OleInitialize(nullptr);

  flutter::DartProject project(L"data");

  std::vector<std::string> command_line_arguments =
      GetCommandLineArguments();

  // Nova mini progress window (IDM parity): `--mini` → compact + always on top.
  bool mini_mode = false;
  for (const auto& arg : command_line_arguments) {
    if (arg == "--mini") mini_mode = true;
  }

  project.set_dart_entrypoint_arguments(std::move(command_line_arguments));

  FlutterWindow window(project);
  Win32Window::Point origin(10, 10);
  Win32Window::Size size(mini_mode ? 340 : 1280, mini_mode ? 460 : 720);
  if (!window.Create(mini_mode ? L"Nova — mini" : L"nova_desktop", origin, size)) {
    return EXIT_FAILURE;
  }
  window.SetQuitOnClose(true);
  if (mini_mode) {
    // HWND_TOPMOST + no taskbar button for the floating progress window.
    ::SetWindowPos(window.GetHandle(), HWND_TOPMOST, 0, 0, 0, 0,
                   SWP_NOMOVE | SWP_NOSIZE);
    LONG style = ::GetWindowLong(window.GetHandle(), GWL_EXSTYLE);
    ::SetWindowLong(window.GetHandle(), GWL_EXSTYLE,
                    style | WS_EX_TOOLWINDOW);
  }

  ::MSG msg;
  while (::GetMessage(&msg, nullptr, 0, 0)) {
    ::TranslateMessage(&msg);
    ::DispatchMessage(&msg);
  }

  ::CoUninitialize();
  return EXIT_SUCCESS;
}
