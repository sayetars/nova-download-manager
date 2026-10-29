#include "flutter_window.h"

#include <optional>
#include <string>
#include <vector>

#include <shellapi.h>

#include "flutter/generated_plugin_registrant.h"
#include "nova_pipe_bridge.h"
#include "resource.h"

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

  // v1.8.0 (IDM tray parity): "nova/tray" — init creates the tray icon,
  // hide/show minimize-to-tray and restore. The menu and left click notify
  // Dart via "onTrayShow" / "onTrayExit".
  tray_channel_ = std::make_unique<flutter::MethodChannel<flutter::EncodableValue>>(
      flutter_controller_->engine()->messenger(), "nova/tray",
      &flutter::StandardMethodCodec::instance());
  tray_channel_->SetMethodCallHandler(
      [this](const flutter::MethodCall<flutter::EncodableValue>& call,
             std::unique_ptr<flutter::MethodResult<flutter::EncodableValue>> result) {
        if (call.method_name() == "init") {
          const bool ok = TrayCreate();
          result->Success(flutter::EncodableValue(ok));
        } else if (call.method_name() == "hide") {
          ::ShowWindow(GetHandle(), SW_HIDE);
          result->Success(flutter::EncodableValue(true));
        } else if (call.method_name() == "show") {
          ::ShowWindow(GetHandle(), SW_SHOW);
          ::SetForegroundWindow(GetHandle());
          result->Success(flutter::EncodableValue(true));
        } else {
          result->NotImplemented();
        }
      });

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
  TrayRemove();
  pipe_bridge_.reset();
  dnd_channel_.reset();
  tray_channel_.reset();
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
    case NOVA_TRAY_MSG: {
      // v1.8.0: tray mouse events. lparam's LOWORD is the mouse message.
      const UINT mouse = LOWORD(lparam);
      if (mouse == WM_LBUTTONUP || mouse == WM_LBUTTONDBLCLK) {
        ::ShowWindow(GetHandle(), SW_SHOW);
        ::SetForegroundWindow(GetHandle());
      } else if (mouse == WM_RBUTTONUP || mouse == WM_CONTEXTMENU) {
        TrayShowMenu();
      }
      return 0;
    }
    case NOVA_TRAY_MENU_SHOW:
      ::ShowWindow(GetHandle(), SW_SHOW);
      ::SetForegroundWindow(GetHandle());
      return 0;
    case NOVA_TRAY_MENU_EXIT:
      // Let Dart run its exit path (didRequestAppExit) for a clean shutdown.
      TrayInvoke("onTrayExit");
      return 0;
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

// ---- v1.8.0 (IDM tray parity) --------------------------------------------

bool FlutterWindow::TrayCreate() {
  if (tray_created_) return true;
  HINSTANCE instance = GetModuleHandle(nullptr);
  tray_icon_ = {};
  tray_icon_.cbSize = sizeof(tray_icon_);
  tray_icon_.hWnd = GetHandle();
  tray_icon_.uID = 1;
  tray_icon_.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
  tray_icon_.uCallbackMessage = NOVA_TRAY_MSG;
  tray_icon_.hIcon =
      LoadIcon(instance, MAKEINTRESOURCE(IDI_APP_ICON));
  if (!tray_icon_.hIcon) tray_icon_.hIcon = LoadIcon(nullptr, IDI_APPLICATION);
  wcscpy_s(tray_icon_.szTip, L"Nova Download Manager");
  tray_created_ = Shell_NotifyIconW(NIM_ADD, &tray_icon_) == TRUE;
  return tray_created_;
}

void FlutterWindow::TrayRemove() {
  if (!tray_created_) return;
  Shell_NotifyIconW(NIM_DELETE, &tray_icon_);
  tray_created_ = false;
}

void FlutterWindow::TrayShowMenu() {
  HMENU menu = CreatePopupMenu();
  AppendMenuW(menu, MF_STRING, NOVA_TRAY_MENU_SHOW, L"Open Nova");
  AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
  AppendMenuW(menu, MF_STRING, NOVA_TRAY_MENU_EXIT, L"Exit");
  POINT cursor;
  GetCursorPos(&cursor);
  // Required so the menu dismisses when clicking elsewhere.
  SetForegroundWindow(GetHandle());
  TrackPopupMenu(menu, TPM_RIGHTBUTTON, cursor.x, cursor.y, 0, GetHandle(),
                 nullptr);
  DestroyMenu(menu);
}

void FlutterWindow::TrayInvoke(const char* method) {
  if (tray_channel_) {
    tray_channel_->InvokeMethod(method, nullptr);
  }
}
