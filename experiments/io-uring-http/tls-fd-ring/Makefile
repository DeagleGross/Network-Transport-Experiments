CC ?= cc
LIBURING_PREFIX ?= $(HOME)/.local
CPPFLAGS += -isystem $(LIBURING_PREFIX)/include
CFLAGS ?= -O2 -g
CFLAGS += -std=c11 -Wall -Wextra -Wpedantic -Werror
LDLIBS += $(LIBURING_PREFIX)/lib/liburing.a -lssl -lcrypto

.PHONY: all sanitize
all: bin/server
bin/server: server.c
	mkdir -p bin
	$(CC) $(CPPFLAGS) $(CFLAGS) $< $(LDLIBS) -o $@
sanitize: bin/server-sanitize
bin/server-sanitize: server.c
	mkdir -p bin
	$(CC) $(CPPFLAGS) $(CFLAGS) -O1 -fsanitize=address,undefined -fno-omit-frame-pointer $< $(LDLIBS) -o $@
