#include "my_application.h"

#include <flutter_linux/flutter_linux.h>
#ifdef GDK_WINDOWING_X11
#include <gdk/gdkx.h>
#endif

#include <flutter/generated_plugin_registrant.h>

struct _MyApplication {
  GtkApplication parent_instance;
  char** dart_entrypoint_arguments;
  FlMethodChannel* dnd_channel;
  FlMethodChannel* window_channel;
  gboolean mini_mode;
};

G_DEFINE_TYPE(MyApplication, my_application, GTK_TYPE_APPLICATION)

// True when the process was launched with the `--mini` argument (progress window).
static gboolean nova_has_mini_arg(MyApplication* self) {
  if (self->dart_entrypoint_arguments == nullptr) return FALSE;
  for (int i = 0; self->dart_entrypoint_arguments[i] != nullptr; i++) {
    if (g_strcmp0(self->dart_entrypoint_arguments[i], "--mini") == 0) return TRUE;
  }
  return FALSE;
}

// Called when first Flutter frame received.
static void first_frame_cb(MyApplication* self, FlView* view) {
  gtk_widget_show(gtk_widget_get_toplevel(GTK_WIDGET(view)));
}

// Nova drag & drop (IDM parity): dropped text/URI lists are forwarded to the
// Dart side on method channel "nova/dnd" as a call to "onDrop".
static void nova_dnd_data_received(GtkWidget* widget, GdkDragContext* context,
                                    gint x, gint y, GtkSelectionData* data,
                                    guint info, guint time, gpointer user_data) {
  MyApplication* self = MY_APPLICATION(user_data);
  if (self->dnd_channel == nullptr) {
    gtk_drag_finish(context, FALSE, FALSE, time);
    return;
  }
  gchar** uris = gtk_selection_data_get_uris(data);
  // Not g_autoptr: ownership transfers into the map via fl_value_set_string_take.
  FlValue* items = fl_value_new_list();
  if (uris != nullptr) {
    for (gint i = 0; uris[i] != nullptr && i < 64; i++) {
      fl_value_append_take(items, fl_value_new_string(uris[i]));
    }
    g_strfreev(uris);
  }
  const guchar* text = gtk_selection_data_get_text(data);
  if (text != nullptr && fl_value_get_length(items) == 0) {
    fl_value_append_take(items, fl_value_new_string((const gchar*)text));
  }
  if (fl_value_get_length(items) > 0) {
    g_autoptr(FlValue) args = fl_value_new_map();
    fl_value_set_string_take(args, "items", items);
    fl_method_channel_invoke_method(self->dnd_channel, "onDrop", args,
                                    nullptr, nullptr, nullptr);
  } else {
    fl_value_unref(items);
  }
  gtk_drag_finish(context, TRUE, FALSE, time);
}

// Implements GApplication::activate.
static void my_application_activate(GApplication* application) {
  MyApplication* self = MY_APPLICATION(application);
  self->mini_mode = nova_has_mini_arg(self);
  GtkWindow* window =
      GTK_WINDOW(gtk_application_window_new(GTK_APPLICATION(application)));

  // Nova mini progress window (IDM parity): compact, always on top, no taskbar entry.
  if (self->mini_mode) {
    gtk_window_set_title(window, "Nova — mini");
    gtk_window_set_default_size(window, 340, 460);
    gtk_window_set_keep_above(window, TRUE);
    gtk_window_set_skip_taskbar_hint(window, TRUE);
  }

  // Use a header bar when running in GNOME as this is the common style used
  // by applications and is the setup most users will be using (e.g. Ubuntu
  // desktop).
  // If running on X and not using GNOME then just use a traditional title bar
  // in case the window manager does more exotic layout, e.g. tiling.
  // If running on Wayland assume the header bar will work (may need changing
  // if future cases occur).
  gboolean use_header_bar = !self->mini_mode;
#ifdef GDK_WINDOWING_X11
  GdkScreen* screen = gtk_window_get_screen(window);
  if (GDK_IS_X11_SCREEN(screen)) {
    const gchar* wm_name = gdk_x11_screen_get_window_manager_name(screen);
    if (g_strcmp0(wm_name, "GNOME Shell") != 0) {
      use_header_bar = FALSE;
    }
  }
#endif
  if (use_header_bar) {
    GtkHeaderBar* header_bar = GTK_HEADER_BAR(gtk_header_bar_new());
    gtk_widget_show(GTK_WIDGET(header_bar));
    gtk_header_bar_set_title(header_bar, "nova_desktop");
    gtk_header_bar_set_show_close_button(header_bar, TRUE);
    gtk_window_set_titlebar(window, GTK_WIDGET(header_bar));
  } else if (!self->mini_mode) {
    gtk_window_set_title(window, "nova_desktop");
  }

  if (!self->mini_mode) {
    gtk_window_set_default_size(window, 1280, 720);
  }

  g_autoptr(FlDartProject) project = fl_dart_project_new();
  fl_dart_project_set_dart_entrypoint_arguments(
      project, self->dart_entrypoint_arguments);

  FlView* view = fl_view_new(project);
  GdkRGBA background_color;
  // Background defaults to black, override it here if necessary, e.g. #00000000
  // for transparent.
  gdk_rgba_parse(&background_color, "#000000");
  fl_view_set_background_color(view, &background_color);
  gtk_widget_show(GTK_WIDGET(view));
  gtk_container_add(GTK_CONTAINER(window), GTK_WIDGET(view));

  // Show the window when Flutter renders.
  // Requires the view to be realized so we can start rendering.
  g_signal_connect_swapped(view, "first-frame", G_CALLBACK(first_frame_cb),
                           self);
  gtk_widget_realize(GTK_WIDGET(view));

  fl_register_plugins(FL_PLUGIN_REGISTRY(view));

  // Nova drag & drop bridge (method channel "nova/dnd").
  self->dnd_channel = fl_method_channel_new(fl_engine_get_binary_messenger(
                                             fl_view_get_engine(view)),
                                             "nova/dnd", FL_METHOD_CODEC(
                                             fl_standard_method_codec_new()));
  // Nova window control bridge (method channel "nova/window"): always-on-top
  // and sizing, invoked by the Dart mini window after boot.
  self->window_channel = fl_method_channel_new(fl_engine_get_binary_messenger(
                                                  fl_view_get_engine(view)),
                                                  "nova/window", FL_METHOD_CODEC(
                                                  fl_standard_method_codec_new()));
  fl_method_channel_set_method_call_handler(
      self->window_channel,
      [](FlMethodChannel* channel, FlMethodCall* call, gpointer user_data) {
        MyApplication* self = MY_APPLICATION(user_data);
        GtkWindow* win =
            GTK_WINDOW(gtk_application_get_active_window(GTK_APPLICATION(self)));
        const gchar* name = fl_method_call_get_name(call);
        FlValue* args = fl_method_call_get_args(call);
        g_autoptr(FlMethodResponse) response = nullptr;
        if (g_strcmp0(name, "setAlwaysOnTop") == 0 && win != nullptr) {
          gboolean top = TRUE;
          FlValueType t = fl_value_get_type(args);
          if (t == FL_VALUE_TYPE_BOOL) {
            top = fl_value_get_bool(args);
          } else if (t == FL_VALUE_TYPE_MAP) {
            FlValue* v = fl_value_lookup_string(args, "value");
            if (v != nullptr) top = fl_value_get_bool(v);
          }
          gtk_window_set_keep_above(win, top);
          response = FL_METHOD_RESPONSE(fl_method_success_response_new(nullptr));
        } else if (g_strcmp0(name, "setSize") == 0 && win != nullptr) {
          double w = 340, h = 460;
          if (fl_value_get_type(args) == FL_VALUE_TYPE_MAP) {
            FlValue* wv = fl_value_lookup_string(args, "width");
            FlValue* hv = fl_value_lookup_string(args, "height");
            if (wv != nullptr) w = fl_value_get_float(wv);
            if (hv != nullptr) h = fl_value_get_float(hv);
          }
          gtk_window_resize(win, (gint)w, (gint)h);
          response = FL_METHOD_RESPONSE(fl_method_success_response_new(nullptr));
        } else {
          response = FL_METHOD_RESPONSE(fl_method_not_implemented_response_new());
        }
        GError* error = nullptr;
        if (!fl_method_call_respond(call, response, &error)) {
          g_warning("nova/window respond failed: %s", error ? error->message : "?");
          g_clear_error(&error);
        }
      },
      self, nullptr);

  if (!self->mini_mode) {
    static GtkTargetEntry nova_dnd_targets[] = {
        {(gchar*)"text/uri-list", 0, 0},
        {(gchar*)"text/plain", 0, 1},
        {(gchar*)"TEXT", 0, 2},
    };
    gtk_drag_dest_set(GTK_WIDGET(view), GTK_DEST_DEFAULT_ALL, nova_dnd_targets,
                      G_N_ELEMENTS(nova_dnd_targets), GDK_ACTION_COPY);
    g_signal_connect(view, "drag-data-received",
                     G_CALLBACK(nova_dnd_data_received), self);
  }

  gtk_widget_grab_focus(GTK_WIDGET(view));
}

// Implements GApplication::local_command_line.
static gboolean my_application_local_command_line(GApplication* application,
                                                  gchar*** arguments,
                                                  int* exit_status) {
  MyApplication* self = MY_APPLICATION(application);
  // Strip out the first argument as it is the binary name.
  self->dart_entrypoint_arguments = g_strdupv(*arguments + 1);

  g_autoptr(GError) error = nullptr;
  if (!g_application_register(application, nullptr, &error)) {
    g_warning("Failed to register: %s", error->message);
    *exit_status = 1;
    return TRUE;
  }

  g_application_activate(application);
  *exit_status = 0;

  return TRUE;
}

// Implements GApplication::startup.
static void my_application_startup(GApplication* application) {
  // MyApplication* self = MY_APPLICATION(object);

  // Perform any actions required at application startup.

  G_APPLICATION_CLASS(my_application_parent_class)->startup(application);
}

// Implements GApplication::shutdown.
static void my_application_shutdown(GApplication* application) {
  // MyApplication* self = MY_APPLICATION(object);

  // Perform any actions required at application shutdown.

  G_APPLICATION_CLASS(my_application_parent_class)->shutdown(application);
}

// Implements GObject::dispose.
static void my_application_dispose(GObject* object) {
  MyApplication* self = MY_APPLICATION(object);
  g_clear_pointer(&self->dart_entrypoint_arguments, g_strfreev);
  G_OBJECT_CLASS(my_application_parent_class)->dispose(object);
}

static void my_application_class_init(MyApplicationClass* klass) {
  G_APPLICATION_CLASS(klass)->activate = my_application_activate;
  G_APPLICATION_CLASS(klass)->local_command_line =
      my_application_local_command_line;
  G_APPLICATION_CLASS(klass)->startup = my_application_startup;
  G_APPLICATION_CLASS(klass)->shutdown = my_application_shutdown;
  G_OBJECT_CLASS(klass)->dispose = my_application_dispose;
}

static void my_application_init(MyApplication* self) {}

MyApplication* my_application_new() {
  // Set the program name to the application ID, which helps various systems
  // like GTK and desktop environments map this running application to its
  // corresponding .desktop file. This ensures better integration by allowing
  // the application to be recognized beyond its binary name.
  g_set_prgname(APPLICATION_ID);

  return MY_APPLICATION(g_object_new(my_application_get_type(),
                                     "application-id", APPLICATION_ID, "flags",
                                     G_APPLICATION_NON_UNIQUE, nullptr));
}
