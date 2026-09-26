CC ?= cc
LIBURING_PREFIX ?= $(HOME)/.local
CPPFLAGS += -isystem $(LIBURING_PREFIX)/include
CFLAGS ?= -O2 -g
CFLAGS += -std=c11 -Wall -Wextra -Wpedantic -Werror
LDLIBS += $(LIBURING_PREFIX)/lib/liburing.a

.PHONY: all sanitize clean
all: bin/oneshot bin/multishot

bin/%: %/server.c
	mkdir -p bin
	$(CC) $(CPPFLAGS) $(CFLAGS) $< $(LDFLAGS) $(LDLIBS) -o $@

bin/sanitize/%: %/server.c
	mkdir -p bin/sanitize
	$(CC) $(CPPFLAGS) $(CFLAGS) -O1 -fsanitize=address,undefined -fno-omit-frame-pointer $< $(LDFLAGS) $(LDLIBS) -o $@

sanitize: bin/sanitize/oneshot bin/sanitize/multishot

clean:
	rm -f bin/oneshot bin/multishot bin/sanitize/oneshot bin/sanitize/multishot
