#ifndef RUNNER_FLUTTER_WINDOW_H_
#define RUNNER_FLUTTER_WINDOW_H_

#include <flutter/dart_project.h>
#include <flutter/flutter_view_controller.h>
#include <flutter/method_channel.h>
#include <flutter/standard_method_codec.h>

#include <memory>

#include "nova_pipe_bridge.h"

#include "win32_window.h"

// v1.8.0 (IDM tray parity): tray callback message (lparam carries the mouse event).
#define NOVA_TRAY_MSG (WM_APP + 1)
#define NOVA_TRAY_MENU_SHOW 4201
#define NOVA_TRAY_MENU_EXIT 4202

// A window that does nothing but host a Flutter view.
class FlutterWindow : public Win32Window {
 public:
  // Creates a new FlutterWindow hosting a Flutter view running |project|.
  explicit FlutterWindow(const flutter::DartProject& project);
  virtual ~FlutterWindow();

 protected:
  // Win32Window:
  bool OnCreate() override;
  void OnDestroy() override;
  LRESULT MessageHandler(HWND window, UINT const message, WPARAM const wparam,
                         LPARAM const lparam) noexcept override;

 private:
  // The project to run.
  flutter::DartProject project_;

  // The Flutter instance hosted by this window.
  std::unique_ptr<flutter::FlutterViewController> flutter_controller_;

  // Nova engine IPC bridge.
  std::unique_ptr<nova::PipeBridge> pipe_bridge_;

  // Nova drag & drop bridge (method channel "nova/dnd").
  std::unique_ptr<flutter::MethodChannel<flutter::EncodableValue>> dnd_channel_;

  // v1.8.0: Windows tray icon + "nova/tray" channel (init/hide/show).
  std::unique_ptr<flutter::MethodChannel<flutter::EncodableValue>> tray_channel_;
  NOTIFYICONDATAW tray_icon_{};
  bool tray_created_ = false;
  bool TrayCreate();
  void TrayRemove();
  void TrayShowMenu();
  void TrayInvoke(const char* method);
};

#endif  // RUNNER_FLUTTER_WINDOW_H_
