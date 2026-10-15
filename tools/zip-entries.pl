#!/usr/bin/env perl
# Reads a zip archive (a .nupkg or .snupkg) with core perl only, so verify_nupkg_metadata needs no
# unzip on the host:
#   zip-entries.pl list <archive>            one entry name per line
#   zip-entries.pl cat  <archive> <entry>    the entry's bytes on stdout; exit 1 if it is missing
use strict;
use warnings;
use IO::Uncompress::Unzip qw($UnzipError);

my ($mode, $archive, $entry) = @ARGV;
die "usage: $0 list <archive> | cat <archive> <entry>\n"
    unless defined $mode && defined $archive && ($mode eq 'list' || ($mode eq 'cat' && defined $entry));

my $zip = IO::Uncompress::Unzip->new($archive) or die "$0: cannot open $archive: $UnzipError\n";
binmode STDOUT;
my $status = 1;
while ($status > 0) {
    my $name = $zip->getHeaderInfo()->{Name};
    if ($mode eq 'list') {
        print "$name\n";
    }
    elsif ($name eq $entry) {
        my $buffer;
        while (($status = $zip->read($buffer)) > 0) {
            print $buffer;
        }
        die "$0: cannot read $entry from $archive: $UnzipError\n" if $status < 0;
        exit 0;
    }
    $status = $zip->nextStream();
}
die "$0: cannot read $archive: $UnzipError\n" if $status < 0;
die "$0: $archive has no entry $entry\n" if $mode eq 'cat';
