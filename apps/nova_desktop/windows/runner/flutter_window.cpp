#include "flutter_window.h"

#include <optional>
#include <string>
#include <vector>

#include <shellapi.h>

#include "flutter/generated_plugin_registrant.h"
#include "nova_pipe_bridge.h"

FlutterWindow::FlutterWindow(const flutter::DartProject& project)
    : project_(project) {}

FlutterWindow::~FlutterWindow() {}

bool FlutterWindow::OnCreate() {
  if (!Win32Window::OnCreate()) {
    return false;
  }

  RECT frame = GetClientArea();

  // The size here must match the window dimensions to avoid unnecessary surface
  // creation / destruction in the startup path.
  flutter_controller_ = std::make_unique<flutter::FlutterViewController>(
      frame.right - frame.left, frame.bottom - frame.top, project_);
  // Ensure that basic setup of the controller was successful.
  if (!flutter_controller_->engine() || !flutter_controller_->view()) {
    return false;
  }
  RegisterPlugins(flutter_controller_->engine());
  SetChildContent(flutter_controller_->view()->GetNativeWindow());

  // Nova engine IPC bridge (named pipe ↔ method channel "nova/pipe").
  pipe_bridge_ = std::make_unique<nova::PipeBridge>(
      flutter_controller_->engine()->messenger());

  // Nova drag & drop bridge: OS drops (files + text/URLs) forwarded on
  // method channel "nova/dnd" as a call to "onDrop" with a string list.
  dnd_channel_ = std::make_unique<flutter::MethodChannel<flutter::EncodableValue>>(
      flutter_controller_->engine()->messenger(), "nova/dnd",
      &flutter::StandardMethodCodec::instance());
  DragAcceptFiles(GetHandle(), TRUE);

  flutter_controller_->engine()->SetNextFrameCallback([&]() {
    this->Show();
  });

  // Flutter can complete the first frame before the "show window" callback is
  // registered. The following call ensures a frame is pending to ensure the
  // window is shown. It is a no-op if the first frame hasn't completed yet.
  flutter_controller_->ForceRedraw();

  return true;
}

void FlutterWindow::OnDestroy() {
  pipe_bridge_.reset();
  dnd_channel_.reset();
  if (flutter_controller_) {
    flutter_controller_ = nullptr;
  }

  Win32Window::OnDestroy();
}

LRESULT
FlutterWindow::MessageHandler(HWND hwnd, UINT const message,
                              WPARAM const wparam,
                              LPARAM const lparam) noexcept {
  // Give Flutter, including plugins, an opportunity to handle window messages.
  if (flutter_controller_) {
    std::optional<LRESULT> result =
        flutter_controller_->HandleTopLevelWindowProc(hwnd, message, wparam,
                                                      lparam);
    if (result) {
      return *result;
    }
  }

  switch (message) {
    case WM_FONTCHANGE:
      flutter_controller_->engine()->ReloadSystemFonts();
      break;
    case WM_DROPFILES: {
      // IDM parity: drop links/files onto the window → Add dialog prefilled.
      HDROP drop = reinterpret_cast<HDROP>(wparam);
      wchar_t path[MAX_PATH];
      const UINT count = DragQueryFile(drop, 0xFFFFFFFF, nullptr, 0);
      std::vector<std::string> items;
      for (UINT i = 0; i < count && i < 64; i++) {
        if (DragQueryFile(drop, i, path, MAX_PATH) > 0) {
          int size_needed =
              WideCharToMultiByte(CP_UTF8, 0, path, -1, nullptr, 0, nullptr, nullptr);
          std::string utf8(size_needed - 1, 0);
          WideCharToMultiByte(CP_UTF8, 0, path, -1, utf8.data(), size_needed,
                              nullptr, nullptr);
          items.push_back(std::move(utf8));
        }
      }
      DragFinish(drop);
      if (!items.empty() && dnd_channel_) {
        flutter::EncodableList list;
        for (auto& s : items) list.emplace_back(flutter::EncodableValue(s));
        dnd_channel_->InvokeMethod(
            "onDrop",
            std::make_unique<flutter::EncodableValue>(flutter::EncodableValue(
                flutter::EncodableMap{{flutter::EncodableValue("items"),
                                       flutter::EncodableValue(list)}})));
      }
      return 0;
    }
  }

  return Win32Window::MessageHandler(hwnd, message, wparam, lparam);
}
