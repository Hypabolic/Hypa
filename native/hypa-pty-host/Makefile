# hypa-pty-host — native PTY helper
# Output path is set by scripts/build-hypa-pty-host.sh via OUTPUT=...

CC      ?= cc
# Keep -std=c11 -Werror. Do not switch to gnu11 or drop Werror.
CFLAGS  ?= -O2 -g -Wall -Wextra -Werror -std=c11
CPPFLAGS += -Iinclude
CPPFLAGS += -D_POSIX_C_SOURCE=200809L -D_XOPEN_SOURCE=700
LDFLAGS ?=

SRCS := src/main.c src/ipc.c src/session.c src/env.c src/fdpass.c
OBJS := $(SRCS:.c=.o)

OUTPUT ?= hypa-pty-host

UNAME_S := $(shell uname -s)
ifeq ($(UNAME_S),Linux)
  # glibc hides posix_openpt / usleep extras under this macro with -std=c11.
  CPPFLAGS += -D_DEFAULT_SOURCE
  LDLIBS += -lutil
endif
ifeq ($(UNAME_S),Darwin)
  # Keep BSD extras (mkdtemp, CMSG_*) visible under -std=c11 + POSIX macros.
  CPPFLAGS += -D_DARWIN_C_SOURCE
endif

.PHONY: all clean

all: $(OUTPUT)

$(OUTPUT): $(OBJS)
	$(CC) $(LDFLAGS) -o $@ $(OBJS) $(LDLIBS)

src/%.o: src/%.c include/pty_host_ipc.h src/ipc.h src/session.h src/env.h src/fdpass.h
	$(CC) $(CPPFLAGS) $(CFLAGS) -c -o $@ $<

clean:
	rm -f $(OBJS) $(OUTPUT)
