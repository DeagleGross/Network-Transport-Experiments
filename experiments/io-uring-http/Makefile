CC ?= cc
LIBURING_PREFIX ?= $(HOME)/.local
CPPFLAGS += -isystem $(LIBURING_PREFIX)/include
CFLAGS ?= -O2 -g
CFLAGS += -std=c11 -Wall -Wextra -Wpedantic -Werror
LDLIBS += $(LIBURING_PREFIX)/lib/liburing.a

.PHONY: all check sanitize clean
all: bin/oneshot bin/multishot

bin/%: %/server.c common/server.c common/server.h
	mkdir -p bin
	$(CC) $(CPPFLAGS) $(CFLAGS) $< common/server.c $(LDFLAGS) $(LDLIBS) -o $@

check: all
	python3 check.py

bin/sanitize/%: %/server.c common/server.c common/server.h
	mkdir -p bin/sanitize
	$(CC) $(CPPFLAGS) $(CFLAGS) -O1 -fsanitize=address,undefined -fno-omit-frame-pointer $< common/server.c $(LDFLAGS) $(LDLIBS) -o $@

sanitize: bin/sanitize/oneshot bin/sanitize/multishot
	SERVER_BIN=$(CURDIR)/bin/sanitize python3 check.py

clean:
	rm -f bin/oneshot bin/multishot bin/sanitize/oneshot bin/sanitize/multishot
